using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Returns a read-only snapshot of the coordinate references in the active project:
    /// immutable internal origin, project base point, survey point, and every project location.
    /// All lengths cross the handler boundary in millimeters and all angles in degrees.
    /// </summary>
    public class GetProjectCoordinateSystemHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;
        private const double RadiansToDegrees = 180.0 / Math.PI;

        public string Name => "get_project_coordinate_system";

        public string Description =>
            "Read the project's Internal Origin, Project Base Point, Survey Point, active Project Location, " +
            "all named Project Locations, True North angles, and site/geographic coordinates.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {}
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            if (doc.IsFamilyDocument)
                return CommandResult.Fail("Project coordinate systems are only available in project documents, not family documents.");

            return CommandResult.Ok(BuildCoordinateSystemDto(doc));
        }

        internal static object BuildCoordinateSystemDto(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (doc.IsFamilyDocument)
                throw new ArgumentException("Coordinate-system snapshots require a project document.", nameof(doc));

            var warnings = new List<string>();

            InternalOrigin internalOrigin = null;
            BasePoint projectBasePoint = null;
            BasePoint surveyPoint = null;
            ProjectLocation activeLocation = null;

            try
            {
                internalOrigin = InternalOrigin.Get(doc);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read Internal Origin: " + ex.Message);
            }

            try
            {
                projectBasePoint = BasePoint.GetProjectBasePoint(doc);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read Project Base Point: " + ex.Message);
            }

            try
            {
                surveyPoint = BasePoint.GetSurveyPoint(doc);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read Survey Point: " + ex.Message);
            }

            try
            {
                activeLocation = doc.ActiveProjectLocation;
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read the active Project Location: " + ex.Message);
            }

            var locations = new List<ProjectLocation>();
            try
            {
                locations = doc.ProjectLocations
                    .Cast<ProjectLocation>()
                    .OrderBy(location => SafeName(location), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                warnings.Add("Could not enumerate Project Locations: " + ex.Message);
                if (activeLocation != null)
                    locations.Add(activeLocation);
            }

            var activeLocationId = activeLocation != null
                ? (long?)RevitCompat.GetId(activeLocation.Id)
                : null;

            var locationDtos = locations
                .GroupBy(location => RevitCompat.GetId(location.Id))
                .Select(group => group.First())
                .Select(location => BuildProjectLocationDto(
                    location,
                    activeLocationId,
                    projectBasePoint,
                    surveyPoint,
                    warnings))
                .ToList();

            return new
            {
                document_title = doc.Title,
                units = new
                {
                    length = "mm",
                    angle = "degrees",
                    latitude_longitude = "degrees"
                },
                coordinate_conventions = new
                {
                    internal_position = "XYZ relative to Revit's immutable Internal Origin.",
                    shared_position = "East/West, North/South, and elevation under the indicated Project Location.",
                    angle_to_true_north = "Rotation from Project North to True North."
                },
                active_project_location = activeLocation == null ? null : new
                {
                    id = RevitCompat.GetId(activeLocation.Id),
                    name = SafeName(activeLocation)
                },
                internal_origin = BuildInternalOriginDto(internalOrigin, warnings),
                project_base_point = BuildBasePointDto(projectBasePoint, false, warnings),
                survey_point = BuildBasePointDto(surveyPoint, true, warnings),
                project_locations = locationDtos,
                warnings
            };
        }

        private static object BuildInternalOriginDto(InternalOrigin origin, List<string> warnings)
        {
            if (origin == null)
                return null;

            try
            {
                return new
                {
                    element_id = RevitCompat.GetId(origin.Id),
                    movable = false,
                    internal_position_mm = ToInternalPointDto(origin.Position),
                    active_shared_position_mm = ToSharedPointDto(origin.SharedPosition)
                };
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read Internal Origin positions: " + ex.Message);
                return new
                {
                    element_id = RevitCompat.GetId(origin.Id),
                    movable = false,
                    internal_position_mm = (object)null,
                    active_shared_position_mm = (object)null
                };
            }
        }

        private static object BuildBasePointDto(BasePoint point, bool isSurveyPoint, List<string> warnings)
        {
            if (point == null)
                return null;

            object internalPosition = null;
            object sharedPosition = null;
            bool? clipped = null;

            try
            {
                internalPosition = ToInternalPointDto(point.Position);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read " + PointLabel(isSurveyPoint) + " internal position: " + ex.Message);
            }

            try
            {
                sharedPosition = ToSharedPointDto(point.SharedPosition);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read " + PointLabel(isSurveyPoint) + " shared position: " + ex.Message);
            }

            if (isSurveyPoint)
            {
                try
                {
                    clipped = point.Clipped;
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not read Survey Point clipped state: " + ex.Message);
                }
            }

            return new
            {
                element_id = RevitCompat.GetId(point.Id),
                is_shared = SafeIsShared(point),
                clipped,
                internal_position_mm = internalPosition,
                active_shared_position_mm = sharedPosition,
                displayed_parameters = ReadDisplayedBasePointParameters(point)
            };
        }

        private static object BuildProjectLocationDto(
            ProjectLocation location,
            long? activeLocationId,
            BasePoint projectBasePoint,
            BasePoint surveyPoint,
            List<string> warnings)
        {
            var locationId = RevitCompat.GetId(location.Id);
            var label = "Project Location '" + SafeName(location) + "'";

            object siteLocation = null;
            try
            {
                siteLocation = BuildSiteLocationDto(location.GetSiteLocation());
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read site data for " + label + ": " + ex.Message);
            }

            return new
            {
                id = locationId,
                name = SafeName(location),
                is_active = activeLocationId.HasValue && activeLocationId.Value == locationId,
                internal_origin_in_shared_coordinates = ReadProjectPosition(location, XYZ.Zero, label + " / Internal Origin", warnings),
                project_base_point_in_shared_coordinates = projectBasePoint == null
                    ? null
                    : ReadProjectPosition(location, SafePosition(projectBasePoint), label + " / Project Base Point", warnings),
                survey_point_in_shared_coordinates = surveyPoint == null
                    ? null
                    : ReadProjectPosition(location, SafePosition(surveyPoint), label + " / Survey Point", warnings),
                site = siteLocation
            };
        }

        private static object ReadProjectPosition(
            ProjectLocation location,
            XYZ internalPoint,
            string label,
            List<string> warnings)
        {
            if (location == null || internalPoint == null)
                return null;

            try
            {
                using (var position = location.GetProjectPosition(internalPoint))
                {
                    if (position == null)
                        return null;

                    return new
                    {
                        east_west_mm = RoundLength(position.EastWest),
                        north_south_mm = RoundLength(position.NorthSouth),
                        elevation_mm = RoundLength(position.Elevation),
                        angle_to_true_north_deg = RoundAngle(position.Angle)
                    };
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Could not calculate shared coordinates for " + label + ": " + ex.Message);
                return null;
            }
        }

        private static object BuildSiteLocationDto(SiteLocation site)
        {
            if (site == null)
                return null;

            return new
            {
                element_id = RevitCompat.GetId(site.Id),
                place_name = site.PlaceName ?? string.Empty,
                latitude_deg = Math.Round(site.Latitude * RadiansToDegrees, 8),
                longitude_deg = Math.Round(site.Longitude * RadiansToDegrees, 8),
                elevation_mm = RoundLength(site.Elevation),
                time_zone_hours = Math.Round(site.TimeZone, 2),
                weather_station_name = site.WeatherStationName ?? string.Empty
            };
        }

        private static object ReadDisplayedBasePointParameters(BasePoint point)
        {
            return new
            {
                east_west_mm = ReadLengthParameter(point, BuiltInParameter.BASEPOINT_EASTWEST_PARAM),
                north_south_mm = ReadLengthParameter(point, BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM),
                elevation_mm = ReadLengthParameter(point, BuiltInParameter.BASEPOINT_ELEVATION_PARAM),
                angle_to_true_north_deg = ReadAngleParameter(point, BuiltInParameter.BASEPOINT_ANGLETON_PARAM)
            };
        }

        private static double? ReadLengthParameter(BasePoint point, BuiltInParameter parameterId)
        {
            try
            {
                var parameter = point.get_Parameter(parameterId);
                return parameter == null ? (double?)null : RoundLength(parameter.AsDouble());
            }
            catch
            {
                return null;
            }
        }

        private static double? ReadAngleParameter(BasePoint point, BuiltInParameter parameterId)
        {
            try
            {
                var parameter = point.get_Parameter(parameterId);
                return parameter == null ? (double?)null : RoundAngle(parameter.AsDouble());
            }
            catch
            {
                return null;
            }
        }

        private static object ToInternalPointDto(XYZ point)
        {
            if (point == null)
                return null;

            return new
            {
                x_mm = RoundLength(point.X),
                y_mm = RoundLength(point.Y),
                z_mm = RoundLength(point.Z)
            };
        }

        private static object ToSharedPointDto(XYZ point)
        {
            if (point == null)
                return null;

            return new
            {
                east_west_mm = RoundLength(point.X),
                north_south_mm = RoundLength(point.Y),
                elevation_mm = RoundLength(point.Z)
            };
        }

        private static XYZ SafePosition(BasePoint point)
        {
            try
            {
                return point?.Position;
            }
            catch
            {
                return null;
            }
        }

        private static bool? SafeIsShared(BasePoint point)
        {
            try
            {
                return point?.IsShared;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeName(Element element)
        {
            try
            {
                return element?.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string PointLabel(bool isSurveyPoint)
        {
            return isSurveyPoint ? "Survey Point" : "Project Base Point";
        }

        private static double RoundLength(double feet)
        {
            return Math.Round(feet * FeetToMm, 3);
        }

        private static double RoundAngle(double radians)
        {
            return Math.Round(radians * RadiansToDegrees, 6);
        }
    }
}
