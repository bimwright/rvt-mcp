using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Reads a link instance's transform and maps its coordinate origins into the host.
    /// Revit links additionally expose the linked document's complete coordinate-system
    /// snapshot so callers can discover linked Project Location ids before publishing.
    /// </summary>
    public class GetLinkCoordinateSystemHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;
        private const double RadiansToDegrees = 180.0 / Math.PI;

        public string Name => "get_link_coordinate_system";

        public string Description =>
            "Read a Revit or CAD link's instance/total transforms and map its origins into host internal/shared coordinates. " +
            "For a loaded Revit link, also returns the linked document's Internal Origin, Project Base Point, Survey Point, " +
            "and all linked Project Locations with ids.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""link_instance_id""],
  ""properties"": {
    ""link_instance_id"": { ""type"": ""integer"", ""description"": ""RevitLinkInstance or linked CAD ImportInstance ElementId in the host document."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var hostDoc = app.ActiveUIDocument?.Document;
            if (hostDoc == null)
                return CommandResult.Fail("No document is open.");

            if (hostDoc.IsFamilyDocument)
                return CommandResult.Fail("Link coordinate systems are only available in project documents, not family documents.");

            long linkInstanceId;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                var idToken = request["link_instance_id"];
                if (idToken == null || !long.TryParse(idToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out linkInstanceId))
                    return CommandResult.Fail("link_instance_id is required and must be an integer.");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (!RevitCompat.CanRepresentElementId(linkInstanceId))
                return CommandResult.Fail("link_instance_id " + RevitCompat.ElementIdRangeError(linkInstanceId));

            var element = hostDoc.GetElement(RevitCompat.ToElementId(linkInstanceId));
            if (element == null)
                return CommandResult.Fail("Element with ID " + linkInstanceId.ToString(CultureInfo.InvariantCulture) + " not found.");

            var revitLink = element as RevitLinkInstance;
            if (revitLink != null)
                return ReadRevitLink(hostDoc, revitLink);

            var cadLink = element as ImportInstance;
            if (cadLink != null)
            {
                if (!cadLink.IsLinked)
                    return CommandResult.Fail("Element " + linkInstanceId.ToString(CultureInfo.InvariantCulture) + " is an imported CAD instance, not a CAD link.");

                return ReadCadLink(hostDoc, cadLink);
            }

            return CommandResult.Fail(
                "Element " + linkInstanceId.ToString(CultureInfo.InvariantCulture) +
                " is not a RevitLinkInstance or linked CAD ImportInstance.");
        }

        private static CommandResult ReadRevitLink(Document hostDoc, RevitLinkInstance link)
        {
            var warnings = new List<string>();
            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null)
            {
                return CommandResult.Fail(
                    "Revit link instance " + RevitCompat.GetId(link.Id).ToString(CultureInfo.InvariantCulture) +
                    " is unloaded or its linked document is unavailable.");
            }

            var instanceTransform = ReadTransform(link, false, warnings);
            var totalTransform = ReadTransform(link, true, warnings);
            var mappingTransform = totalTransform ?? instanceTransform;

            object linkedCoordinateSystem;
            try
            {
                linkedCoordinateSystem = GetProjectCoordinateSystemHandler.BuildCoordinateSystemDto(linkDoc);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not build the linked document coordinate-system snapshot: " + ex.Message);
                linkedCoordinateSystem = null;
            }

            return CommandResult.Ok(new
            {
                link_instance = new
                {
                    element_id = RevitCompat.GetId(link.Id),
                    type_id = RevitCompat.GetId(link.GetTypeId()),
                    name = SafeName(link),
                    kind = "revit_link",
                    is_loaded = true,
                    linked_document_title = linkDoc.Title
                },
                units = new
                {
                    length = "mm",
                    angle = "degrees"
                },
                transforms = new
                {
                    instance_transform = ToTransformDto(instanceTransform),
                    total_transform = ToTransformDto(totalTransform),
                    mapping_uses = totalTransform != null ? "total_transform" : "instance_transform"
                },
                origin_mapping_to_host = BuildRevitOriginMapping(hostDoc, linkDoc, mappingTransform, warnings),
                linked_document_coordinate_system = linkedCoordinateSystem,
                warnings
            });
        }

        private static CommandResult ReadCadLink(Document hostDoc, ImportInstance link)
        {
            var warnings = new List<string>();
            var instanceTransform = ReadTransform(link, false, warnings);
            var totalTransform = ReadTransform(link, true, warnings);
            var mappingTransform = totalTransform ?? instanceTransform;

            warnings.Add(
                "CAD links do not expose a linked Revit Document, Project Locations, Project Base Point, or Survey Point. " +
                "Only the CAD origin and instance transforms are available.");

            return CommandResult.Ok(new
            {
                link_instance = new
                {
                    element_id = RevitCompat.GetId(link.Id),
                    type_id = RevitCompat.GetId(link.GetTypeId()),
                    name = SafeName(link),
                    kind = "cad_link",
                    is_loaded = true
                },
                units = new
                {
                    length = "mm",
                    angle = "degrees"
                },
                transforms = new
                {
                    instance_transform = ToTransformDto(instanceTransform),
                    total_transform = ToTransformDto(totalTransform),
                    mapping_uses = totalTransform != null ? "total_transform" : "instance_transform"
                },
                origin_mapping_to_host = new
                {
                    cad_origin = BuildMappedPoint(hostDoc, XYZ.Zero, mappingTransform, "CAD origin", warnings)
                },
                linked_document_coordinate_system = (object)null,
                warnings
            });
        }

        private static object BuildRevitOriginMapping(
            Document hostDoc,
            Document linkDoc,
            Transform mappingTransform,
            List<string> warnings)
        {
            XYZ internalOrigin = null;
            XYZ projectBasePoint = null;
            XYZ surveyPoint = null;

            try
            {
                internalOrigin = InternalOrigin.Get(linkDoc)?.Position;
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read linked Internal Origin position: " + ex.Message);
            }

            try
            {
                projectBasePoint = BasePoint.GetProjectBasePoint(linkDoc)?.Position;
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read linked Project Base Point position: " + ex.Message);
            }

            try
            {
                surveyPoint = BasePoint.GetSurveyPoint(linkDoc)?.Position;
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read linked Survey Point position: " + ex.Message);
            }

            return new
            {
                linked_internal_origin = BuildMappedPoint(hostDoc, internalOrigin, mappingTransform, "linked Internal Origin", warnings),
                linked_project_base_point = BuildMappedPoint(hostDoc, projectBasePoint, mappingTransform, "linked Project Base Point", warnings),
                linked_survey_point = BuildMappedPoint(hostDoc, surveyPoint, mappingTransform, "linked Survey Point", warnings)
            };
        }

        private static object BuildMappedPoint(
            Document hostDoc,
            XYZ linkedInternalPoint,
            Transform mappingTransform,
            string label,
            List<string> warnings)
        {
            if (linkedInternalPoint == null || mappingTransform == null)
                return null;

            try
            {
                var hostInternalPoint = mappingTransform.OfPoint(linkedInternalPoint);
                return new
                {
                    linked_internal_position_mm = ToXyzDto(linkedInternalPoint),
                    host_internal_position_mm = ToXyzDto(hostInternalPoint),
                    host_active_shared_position = ReadHostSharedPosition(hostDoc, hostInternalPoint, label, warnings)
                };
            }
            catch (Exception ex)
            {
                warnings.Add("Could not map " + label + " into host coordinates: " + ex.Message);
                return null;
            }
        }

        private static object ReadHostSharedPosition(
            Document hostDoc,
            XYZ hostInternalPoint,
            string label,
            List<string> warnings)
        {
            try
            {
                var location = hostDoc.ActiveProjectLocation;
                if (location == null)
                    return null;

                using (var position = location.GetProjectPosition(hostInternalPoint))
                {
                    if (position == null)
                        return null;

                    return new
                    {
                        project_location_id = RevitCompat.GetId(location.Id),
                        project_location_name = SafeName(location),
                        east_west_mm = RoundLength(position.EastWest),
                        north_south_mm = RoundLength(position.NorthSouth),
                        elevation_mm = RoundLength(position.Elevation),
                        angle_to_true_north_deg = RoundAngle(position.Angle)
                    };
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Could not map " + label + " into host shared coordinates: " + ex.Message);
                return null;
            }
        }

        private static Transform ReadTransform(Instance instance, bool total, List<string> warnings)
        {
            try
            {
                return total ? instance.GetTotalTransform() : instance.GetTransform();
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read " + (total ? "total" : "instance") + " transform: " + ex.Message);
                return null;
            }
        }

        private static object ToTransformDto(Transform transform)
        {
            if (transform == null)
                return null;

            return new
            {
                origin_mm = ToXyzDto(transform.Origin),
                basis_x = ToBasisDto(transform.BasisX),
                basis_y = ToBasisDto(transform.BasisY),
                basis_z = ToBasisDto(transform.BasisZ),
                is_identity = transform.IsIdentity,
                has_reflection = transform.HasReflection,
                scale = Math.Round(transform.Scale, 9)
            };
        }

        private static object ToXyzDto(XYZ point)
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

        private static object ToBasisDto(XYZ vector)
        {
            if (vector == null)
                return null;

            return new
            {
                x = Math.Round(vector.X, 9),
                y = Math.Round(vector.Y, 9),
                z = Math.Round(vector.Z, 9)
            };
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
