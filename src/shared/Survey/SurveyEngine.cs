// Read-only relationship observations. No transactions, persistent snapshots or history writes.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Survey
{
    public sealed class SurveyEngine
    {
        readonly UIApplication app;
        readonly Document doc;
        readonly SurveyOptions options;
        readonly SurveyBudget budget;
        readonly Dictionary<string, SurveyRelation> relations = new Dictionary<string, SurveyRelation>();
        readonly Dictionary<long, Element> targets = new Dictionary<long, Element>();
        readonly JArray targetJson = new JArray();
        readonly HashSet<long> scope = new HashSet<long>();
        readonly List<string> blind = new List<string>();
        Phase phase;
        bool missing, touchesType, touchesGroup, touchesDatum, scopeIncomplete;
        int scopeLowerBound, links;
        public static JObject Run(UIApplication app, JObject input) => new SurveyEngine(app, SurveyOptions.Parse(input)).Execute();
        SurveyEngine(UIApplication a, SurveyOptions o)
        {
            app = a;
            doc = a.ActiveUIDocument?.Document;
            if (doc == null)
                throw new ArgumentException("No document is open.");
            options = o;
            budget = new SurveyBudget(o);
            foreach (var kind in SurveyContract.Kinds)
                relations[kind] = new SurveyRelation(kind, o);
        }

        static long Id(ElementId id) => id == null ? -1 : RevitCompat.GetId(id);
        static ElementId Eid(long id) => RevitCompat.ToElementId(id);
        static string Text(string s) => s == null ? null : s.Length <= 160 ? s : s.Substring(0, 160);
        void RunCheck(string kind, string name, long? target, Action<SurveyCheck> read)
        {
            var check = relations[kind].Check(name, target);
            var started = budget.ElapsedMs;
            var scanned = budget.Scanned;
            try
            {
                budget.Check();
                read(check);
            }
            catch (SurveyLimitException ex)
            {
                check.Partial(ex.Message);
            }
            catch (Exception ex)
            {
                check.Partial("api_error:" + ex.GetType().Name);
            }
            finally
            {
                check.ElapsedMs = budget.ElapsedMs - started;
                check.ScannedElements = budget.Scanned - scanned;
            }
        }

        void Edge(string kind, long root, ElementId related, string via, int depth)
        {
            var id = Id(related);
            if (id == -1 || id == root)
                return;
            relations[kind].Add(root, id, via, depth);
            if (kind == "type" || kind == "group" || kind == "join" || kind == "connector")
            {
                if (scope.Count >= options.MaxNodes && !scope.Contains(id))
                {
                    scopeIncomplete = true;
                    throw new SurveyLimitException("scope_node_budget_exhausted");
                }

                scope.Add(id);
            }
        }

        JObject Execute()
        {
            ResolveTargets();
            ResolvePhase();
            RunCheck("host", "loaded_links", null, c =>
            {
                using (var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                    links = collector.GetElementCount();
            });
            if (links > 0)
                blind.Add("linked_document_contents_and_phase_mapping_not_surveyed");
            // View iteration can trigger an uninterruptible native regeneration. Collect bounded local evidence first.
            Types();
            Hosts();
            Groups();
            Joins();
            Connectors();
            Datums();
            Spatial();
            Annotations();
            Proximity();
            Presentation();
            SurveyContract.FinalizeCoverage(relations, missing);
            scopeIncomplete |= missing || new[]
            {
                "type",
                "group",
                "join",
                "connector"
            }.Any(k => !relations[k].Complete);
            scopeLowerBound = Math.Max(scopeLowerBound, scope.Count);
            bool complete = relations.Values.All(r => r.Complete);
            var flags = SurveyContract.StopFlags(options, scopeLowerBound, !scopeIncomplete, complete, touchesType, touchesGroup, touchesDatum, missing);
            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["surveyId"] = Guid.NewGuid().ToString("N"),
                ["context"] = new JObject
                {
                    ["title"] = Text(doc.Title),
                    ["revitVersion"] = app.Application.VersionNumber,
                    ["isFamilyDocument"] = doc.IsFamilyDocument,
                    ["activeViewId"] = Id(doc.ActiveView?.Id),
                    ["phaseId"] = phase == null ? (JToken)JValue.CreateNull() : new JValue(Id(phase.Id)),
                    ["phaseSource"] = options.PhaseId.HasValue ? "explicit" : phase == null ? "unresolved" : "active_view",
                    ["viewScope"] = options.ViewScope,
                    ["depth"] = options.Depth,
                    ["targetScope"] = "active_document",
                    ["linkScope"] = "references_only",
                    ["loadedLinkInstances"] = links
                },
                ["targets"] = targetJson,
                ["relations"] = new JArray(SurveyContract.Kinds.Select(k => relations[k].Json())),
                ["blindSpots"] = new JArray(new[] { "engineering_calculations", "design_intent", "cost", "construction_programme", "future_change_propagation", "pixel_visibility_and_schedule_rows", "unmodeled_constraints" }.Concat(blind).Distinct()),
                ["stopFlags"] = flags,
                ["scope"] = new JObject
                {
                    ["threshold"] = options.ScopeThreshold,
                    ["count"] = scopeLowerBound,
                    ["countKind"] = scopeIncomplete ? "lower_bound" : "exact",
                    ["meaning"] = "targets_and_discovered_propagation_candidates_not_predicted_changes"
                },
                ["limits"] = new JObject
                {
                    ["budgetMs"] = options.BudgetMs,
                    ["nativeCallPreemption"] = false,
                    ["maxViews"] = options.MaxViews,
                    ["maxScannedElements"] = options.MaxScanned,
                    ["scannedElements"] = budget.Scanned,
                    ["maxGraphNodesPerRelation"] = options.MaxNodes,
                    ["maxIdsPerRelation"] = options.MaxIds,
                    ["proximityPaddingMm"] = options.PaddingMm
                },
                ["snapshotUse"] = "observation_only_not_trusted_history_baseline",
                ["complete"] = complete,
                ["elapsedMs"] = budget.ElapsedMs,
                ["outputTrimmed"] = false
            };
            return SurveyContract.Bound(report);
        }

        void ResolveTargets()
        {
            foreach (var raw in options.ElementIds)
            {
                try
                {
                    var e = doc.GetElement(Eid(raw));
                    if (e == null)
                        throw new ArgumentException("not_found");
                    targets[raw] = e;
                    scope.Add(raw);
                    touchesType |= options.ChangeKind == "type" || e is ElementType;
                    touchesGroup |= e is Group || e is GroupType || Id(e.GroupId) != -1;
                    touchesDatum |= e is Level || e is Grid || e is ReferencePlane;
                    var effective = options.ChangeKind == "type" && !(e is ElementType) ? doc.GetElement(e.GetTypeId()) : e;
                    if (effective == null)
                    {
                        missing = true;
                    }

                    targetJson.Add(new JObject { ["id"] = raw, ["status"] = effective == null ? "type_unresolved" : "resolved", ["uniqueId"] = e.UniqueId, ["category"] = Text(e.Category?.Name), ["changeKind"] = options.ChangeKind, ["typeId"] = e is ElementType ? raw : Id(e.GetTypeId()), ["effectiveTargetId"] = effective == null ? (JToken)JValue.CreateNull() : new JValue(Id(effective.Id)), ["parameters"] = Parameters(effective ?? e), ["snapshotCoverage"] = "selected_builtin_parameters_only" });
                }
                catch (Exception ex)
                {
                    targets.Remove(raw);
                    scope.Remove(raw);
                    missing = true;
                    targetJson.Add(new JObject { ["id"] = raw, ["status"] = "not_resolved", ["reason"] = ex is ArgumentException ? "not_found_or_invalid_id" : "api_error:" + ex.GetType().Name });
                }
            }
        }

        JArray Parameters(Element e)
        {
            var result = new JArray();
            foreach (var key in new[]
            {
                BuiltInParameter.ALL_MODEL_MARK,
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                BuiltInParameter.ALL_MODEL_TYPE_COMMENTS,
                BuiltInParameter.ELEM_TYPE_PARAM
            }

            )
            {
                var item = new JObject
                {
                    ["builtInId"] = (int)key,
                    ["ownerId"] = Id(e.Id)
                };
                result.Add(item);
                try
                {
                    var p = e.get_Parameter(key);
                    if (p == null)
                    {
                        item["status"] = "unavailable";
                        continue;
                    }

                    item["status"] = "read";
                    item["name"] = Text(p.Definition?.Name);
                    item["storageType"] = p.StorageType.ToString();
                    item["hasValue"] = p.HasValue;
                    item["value"] = p.StorageType == StorageType.String ? (JToken)new JValue(Text(p.AsString())) : p.StorageType == StorageType.Integer ? new JValue(p.AsInteger()) : p.StorageType == StorageType.Double ? new JValue(p.AsDouble()) : p.StorageType == StorageType.ElementId ? new JValue(Id(p.AsElementId())) : JValue.CreateNull();
                    item["display"] = Text(p.AsValueString());
                    if (p.StorageType == StorageType.String)
                        item["valueTruncated"] = (p.AsString()?.Length ?? 0) > 160;
                }
                catch (Exception ex)
                {
                    item["status"] = "not_checked";
                    item["reason"] = "api_error:" + ex.GetType().Name;
                }
            }

            return result;
        }

        void ResolvePhase()
        {
            try
            {
                var id = options.PhaseId.HasValue ? Eid(options.PhaseId.Value) : doc.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                phase = id == null ? null : doc.GetElement(id) as Phase;
            }
            catch (Exception ex)
            {
                phase = null;
                blind.Add("phase_api_error:" + ex.GetType().Name);
            }

            if (phase == null)
                blind.Add("phase_unresolved_supply_phaseId_for_spatial_queries");
        }

        void Types()
        {
            var types = new Dictionary<long, List<long>>();
            var counts = new Dictionary<long, int>();
            foreach (var entry in targets)
            {
                var type = entry.Value is ElementType ? entry.Value.Id : entry.Value.GetTypeId();
                var tid = Id(type);
                RunCheck("type", "type_reference", entry.Key, c =>
                {
                    if (tid != -1)
                        Edge("type", entry.Key, type, "type", 1);
                });
                if (tid != -1)
                {
                    if (!types.ContainsKey(tid))
                    {
                        types[tid] = new List<long>();
                        counts[tid] = 0;
                    }

                    types[tid].Add(entry.Key);
                }
            }

            var scanned = new HashSet<long>();
            var familyTypes = new HashSet<long>(types.Keys.Where(tid => doc.GetElement(Eid(tid)) is FamilySymbol));
            void Record(Element e, long tid)
            {
                counts[tid]++;
                foreach (var root in types[tid])
                    if (options.Depth == 2 || targets[root] is ElementType)
                        Edge("type", root, e.Id, "type_instance", targets[root] is ElementType ? 1 : 2);
            }

            // This native filter has a defined FamilySymbol contract. Do not extend it to arbitrary types.
            foreach (var tid in familyTypes)
                RunCheck("type", "family_instances_by_symbol", tid, c =>
                {
                    using (var filter = new FamilyInstanceFilter(doc, Eid(tid)))
                    using (var all = new FilteredElementCollector(doc).WherePasses(filter))
                        foreach (var e in all)
                        {
                            budget.Scan();
                            if (Id(e.GetTypeId()) != tid)
                            {
                                c.Partial("family_filter_type_mismatch");
                                continue;
                            }

                            Record(e, tid);
                        }

                    if (c.Complete)
                        scanned.Add(tid);
                });
            var otherTypes = new HashSet<long>(types.Keys.Except(familyTypes));
            if (otherTypes.Count > 0)
                RunCheck("type", "instances_by_GetTypeId", null, c =>
                {
                    // Unknown/system classes retain one shared full-document pass; parameter filters can count centerlines.
                    using (var all = new FilteredElementCollector(doc).WhereElementIsNotElementType())
                        foreach (var e in all)
                        {
                            budget.Scan();
                            var tid = Id(e.GetTypeId());
                            if (otherTypes.Contains(tid))
                                Record(e, tid);
                        }

                    scanned.UnionWith(otherTypes);
                });
            foreach (var item in targetJson.OfType<JObject>().Where(t => t.Value<string>("status") == "resolved"))
            {
                var type = item.Value<long>("typeId");
                if (!counts.ContainsKey(type))
                    continue;
                item["typeInstanceCount"] = counts[type];
                item["typeInstanceCountKind"] = scanned.Contains(type) ? "exact" : "lower_bound";
                item["typeCountMethod"] = familyTypes.Contains(type) ? "family_symbol_filter_verified_by_GetTypeId" : "full_document_GetTypeId";
                scopeLowerBound = Math.Max(scopeLowerBound, counts[type]);
                if (options.Depth == 1 && counts[type] > 1)
                {
                    scopeIncomplete = true;
                    item["typePeerExpansion"] = "not_requested_at_depth1";
                }
            }
        }

        void Hosts()
        {
            foreach (var pair in targets)
                RunCheck("host", "host_and_nested_family", pair.Key, c =>
                {
                    var e = pair.Value;
                    if (e is FamilyInstance fi)
                    {
                        Edge("host", pair.Key, fi.Host?.Id, "host", 1);
                        Edge("host", pair.Key, fi.SuperComponent?.Id, "super_component", 1);
                        foreach (var id in fi.GetSubComponentIds())
                            Edge("host", pair.Key, id, "sub_component", 1);
                        var face = fi.HostFace;
                        if (face != null && Id(face.LinkedElementId) != -1)
                            c.Partial("linked_face_host_contents_not_checked");
                    }
                    else if (!(e is HostObject) && !(e is ElementType))
                        c.Partial("host_adapter_not_available_for_this_class");
                    if (e is HostObject host)
                        foreach (var id in host.FindInserts(true, true, true, true))
                            Edge("host", pair.Key, id, "host_insert", 1);
                });
            RunCheck("host", "reverse_family_hosts", null, c =>
            {
                using (var all = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                    foreach (FamilyInstance fi in all)
                    {
                        budget.Scan();
                        var host = fi.Host;
                        var hid = Id(host?.Id);
                        if (targets.ContainsKey(hid))
                            Edge("host", hid, fi.Id, "hosted_family", 1);
                    }
            });
            relations["host"].Skip("other_hosted_classes", "curtain_nested_nonshared_rebar_and_other_host_classes_not_exhaustively_supported");
        }

        void Groups()
        {
            foreach (var pair in targets)
                RunCheck("group", "group_and_assembly", pair.Key, c =>
                {
                    var e = pair.Value;
                    var g = e as Group ?? doc.GetElement(e.GroupId) as Group;
                    if (g != null)
                    {
                        Edge("group", pair.Key, g.Id, "group", 1);
                        if (e is Group || options.Depth == 2)
                            Edge("group", pair.Key, g.GroupType?.Id, "group_type", e is Group ? 1 : 2);
                        if (e is Group || options.Depth == 2)
                            foreach (var id in g.GetMemberIds())
                            {
                                budget.Scan();
                                Edge("group", pair.Key, id, "group_member", e is Group ? 1 : 2);
                            }

                        if (e is Group && options.Depth == 2)
                            foreach (Group peer in g.GroupType.Groups)
                            {
                                budget.Scan();
                                Edge("group", pair.Key, peer.Id, "same_group_type", 2);
                            }

                        if (!(e is Group) && g.GroupType.Groups.Size > 1)
                            c.Partial("sibling_group_instances_beyond_depth2");
                        if (g.IsAttached || Id(g.AttachedParentId) != -1)
                            c.Partial("attached_group_mapping_not_checked");
                        c.Partial("excluded_nested_and_parameter_variation_semantics_not_checked");
                    }

                    if (e is GroupType gt)
                    {
                        c.Partial("excluded_nested_and_parameter_variation_semantics_not_checked");
                        foreach (Group peer in gt.Groups)
                        {
                            budget.Scan();
                            Edge("group", pair.Key, peer.Id, "group_type_instance", 1);
                            if (options.Depth == 2)
                                foreach (var id in peer.GetMemberIds())
                                {
                                    budget.Scan();
                                    Edge("group", pair.Key, id, "group_member", 2);
                                }
                        }
                    }

                    var assembly = e as AssemblyInstance ?? doc.GetElement(e.AssemblyInstanceId) as AssemblyInstance;
                    if (assembly != null)
                    {
                        Edge("group", pair.Key, assembly.Id, "assembly", 1);
                        if (e is AssemblyInstance || options.Depth == 2)
                            foreach (var id in assembly.GetMemberIds())
                            {
                                budget.Scan();
                                Edge("group", pair.Key, id, "assembly_member", e is AssemblyInstance ? 1 : 2);
                            }
                    }
                });
        }

        IEnumerable<ElementId> Joined(Element e, SurveyCheck c)
        {
            if (doc.IsFamilyDocument)
            {
                c.Partial("geometric_joins_unavailable_in_family_document");
                return new ElementId[0];
            }

            var ids = new List<ElementId>(JoinGeometryUtils.GetJoinedElements(doc, e));
            if (e.Location is LocationCurve curve)
                for (int i = 0; i < 2; i++)
                    foreach (Element peer in curve.get_ElementsAtJoin(i))
                        ids.Add(peer.Id);
#if REVIT2026 || REVIT2027_OR_GREATER
            if (e is Wall wall)
            {
                ids.AddRange(wall.GetAttachmentIds(AttachmentLocation.Top));
                ids.AddRange(wall.GetAttachmentIds(AttachmentLocation.Base));
            }

#else
            if(e is Wall)c.Partial("wall_attachment_api_unavailable_on_this_sdk");
#endif
            if (e is FamilyInstance fi && fi.StructuralType == Autodesk.Revit.DB.Structure.StructuralType.Column)
            {
                for (int end = 0; end < 2; end++)
                {
                    var attachment = ColumnAttachment.GetColumnAttachment(fi, end);
                    if (attachment != null)
                        ids.Add(attachment.TargetId);
                }
            }

            return ids.Where(id => Id(id) != Id(e.Id)).Distinct();
        }

        void Joins()
        {
            foreach (var pair in targets)
                RunCheck("join", "geometry_end_join_and_supported_attachments", pair.Key, c =>
                {
                    var first = Joined(pair.Value, c).ToArray();
                    foreach (var id in first)
                    {
                        budget.Scan();
                        Edge("join", pair.Key, id, "join_or_attachment", 1);
                    }

                    if (options.Depth == 2)
                        foreach (var id in first)
                        {
                            budget.Scan();
                            var peer = doc.GetElement(id);
                            if (peer != null)
                                foreach (var next in Joined(peer, c))
                                {
                                    budget.Scan();
                                    Edge("join", pair.Key, next, "join_or_attachment", 2);
                                }
                        }
                });
            relations["join"].Skip("cuts_coping_and_reverse_attachments", "not_exhaustively_supported");
        }

        ConnectorManager Manager(Element e) => e is MEPCurve curve ? curve.ConnectorManager : e is FamilyInstance fi ? fi.MEPModel?.ConnectorManager : e is FabricationPart part ? part.ConnectorManager : null;
        IEnumerable<ElementId> PhysicalPeers(Element e, SurveyCheck check)
        {
            var manager = Manager(e);
            var result = new HashSet<ElementId>();
            if (manager == null)
            {
                if (e is MEPCurve || e is FabricationPart)
                    check.Partial("connector_manager_unavailable");
                return result;
            }

            foreach (Connector connector in manager.Connectors)
            {
                budget.Scan();
                if (connector.ConnectorType == ConnectorType.Logical)
                {
                    check.Partial("logical_system_topology_not_traversed");
                    continue;
                }

                foreach (Connector other in connector.AllRefs)
                {
                    budget.Scan();
                    if (other.ConnectorType == ConnectorType.Logical)
                    {
                        check.Partial("logical_system_topology_not_traversed");
                        continue;
                    }

                    if (other.ConnectorType != ConnectorType.End && other.ConnectorType != ConnectorType.Curve && other.ConnectorType != ConnectorType.Physical)
                    {
                        check.Partial("unsupported_connector_type");
                        continue;
                    }

                    if (other.Owner != null && Id(other.Owner.Id) != Id(e.Id) && connector.IsConnectedTo(other))
                        result.Add(other.Owner.Id);
                }
            }

            return result;
        }

        void Connectors()
        {
            foreach (var pair in targets)
                RunCheck("connector", "physical_connector_owners", pair.Key, c =>
                {
                    var first = PhysicalPeers(pair.Value, c).ToArray();
                    foreach (var id in first)
                        Edge("connector", pair.Key, id, "physical_connector", 1);
                    if (options.Depth == 2)
                        foreach (var id in first)
                        {
                            budget.Scan();
                            var peer = doc.GetElement(id);
                            if (peer != null)
                                foreach (var next in PhysicalPeers(peer, c))
                                    Edge("connector", pair.Key, next, "physical_connector", 2);
                        }
                });
        }

        void Datums()
        {
            foreach (var pair in targets)
                RunCheck("datum", "level_and_builtin_constraints", pair.Key, c =>
                {
                    var e = pair.Value;
                    Edge("datum", pair.Key, e.LevelId, "level", 1);
                    foreach (var p in new[]
                    {
                        BuiltInParameter.FAMILY_LEVEL_PARAM,
                        BuiltInParameter.WALL_BASE_CONSTRAINT,
                        BuiltInParameter.WALL_HEIGHT_TYPE,
                        BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                        BuiltInParameter.FAMILY_TOP_LEVEL_PARAM,
                        BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM
                    }

                    )
                    {
                        var parameter = e.get_Parameter(p);
                        if (parameter?.StorageType == StorageType.ElementId)
                        {
                            var id = parameter.AsElementId();
                            if (doc.GetElement(id) is Level)
                                Edge("datum", pair.Key, id, "level_parameter:" + (int)p, 1);
                        }
                    }
                });
            relations["datum"].Skip("grid_reference_plane_reverse_constraints", "not_exhaustively_supported");
        }

        void Spatial()
        {
            foreach (var pair in targets)
                RunCheck("spatial", "family_room_space_from_to", pair.Key, c =>
                {
                    if (phase == null)
                    {
                        c.Partial("phase_unresolved");
                        return;
                    }

                    if (pair.Value is FamilyInstance fi)
                    {
                        var cat = Id(fi.Category?.Id);
                        if (cat == (long)BuiltInCategory.OST_Doors || cat == (long)BuiltInCategory.OST_Windows)
                        {
                            Edge("spatial", pair.Key, fi.get_FromRoom(phase)?.Id, "from_room", 1);
                            Edge("spatial", pair.Key, fi.get_ToRoom(phase)?.Id, "to_room", 1);
                        }
                        else
                            Edge("spatial", pair.Key, fi.get_Room(phase)?.Id, "room_at_family_calculation_point", 1);
                        Edge("spatial", pair.Key, fi.get_Space(phase)?.Id, "space_at_family_calculation_point", 1);
                    }
                    else
                        c.Partial("volumetric_containment_not_checked_for_this_class");
                });
            RunCheck("spatial", "reverse_room_space_area_boundaries", null, c =>
            {
                if (phase == null)
                {
                    c.Partial("phase_unresolved");
                    return;
                }

                using (var all = new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)))
                    foreach (SpatialElement spatial in all)
                    {
                        budget.Scan();
                        if (spatial is Area)
                        {
                            c.Partial("area_scheme_boundaries_not_checked");
                            continue;
                        }

                        var phaseParameter = spatial.get_Parameter(BuiltInParameter.ROOM_PHASE_ID);
                        if (phaseParameter == null || phaseParameter.StorageType != StorageType.ElementId)
                        {
                            c.Partial("spatial_phase_parameter_unavailable");
                            continue;
                        }

                        if (phaseParameter.AsElementId() != phase.Id)
                            continue;
                        var boundaries = spatial.GetBoundarySegments(new SpatialElementBoundaryOptions());
                        if (boundaries == null)
                            continue;
                        foreach (var loop in boundaries)
                            foreach (var segment in loop)
                            {
                                budget.Scan();
                                var id = Id(segment.ElementId);
                                if (targets.ContainsKey(id))
                                    Edge("spatial", id, spatial.Id, "spatial_boundary", 1);
                            }
                    }
            });
            relations["spatial"].Skip("area_and_full_volume_overlap", "not_checked_no_inferred_room_containment");
        }

        void Annotations()
        {
            RunCheck("annotations", "independent_tags", null, c =>
            {
                using (var all = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
                    foreach (IndependentTag tag in all)
                    {
                        budget.Scan();
                        foreach (var id in tag.GetTaggedLocalElementIds())
                        {
                            var raw = Id(id);
                            if (targets.ContainsKey(raw))
                                Edge("annotations", raw, tag.Id, "tag_reference", 1);
                        }
                    }
            });
            RunCheck("annotations", "dimension_and_spot_references", null, c =>
            {
                using (var all = new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
                    foreach (Dimension dimension in all)
                    {
                        budget.Scan();
                        var refs = dimension.References;
                        if (refs == null)
                            continue;
                        foreach (Reference reference in refs)
                        {
                            budget.Scan();
                            var id = Id(reference.ElementId);
                            if (targets.ContainsKey(id))
                                Edge("annotations", id, dimension.Id, dimension is SpotDimension ? "spot_reference" : "dimension_reference", 1);
                        }
                    }
            });
            // Spatial tag API subclasses are not accepted by ElementClassFilter.
            foreach (var category in new[]
            {
                BuiltInCategory.OST_RoomTags,
                BuiltInCategory.OST_MEPSpaceTags,
                BuiltInCategory.OST_AreaTags
            }

            )
                RunCheck("annotations", category.ToString(), null, c =>
                {
                    using (var all = new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType())
                        foreach (var tag in all)
                        {
                            budget.Scan();
                            var referenced = tag is RoomTag rt ? rt.Room as Element : tag is SpaceTag st ? st.Space : tag is AreaTag at ? at.Area : null;
                            var id = Id(referenced?.Id);
                            if (targets.ContainsKey(id))
                                Edge("annotations", id, tag.Id, "spatial_tag_reference", 1);
                        }
                });
            if (targets.Values.Any(e => e is RevitLinkInstance))
                relations["annotations"].Skip("linked_tag_subreferences", "linked_element_targets_not_supported");
        }

        void Presentation()
        {
            foreach (var pair in targets)
                RunCheck("presentation", "owner_view", pair.Key, c => Edge("presentation", pair.Key, pair.Value.OwnerViewId, "owner_view", 1));
            RunCheck("presentation", "view_and_schedule_candidates", null, c =>
            {
                if (options.MaxViews == 0)
                {
                    c.Partial("view_scan_not_requested_set_maxViews");
                    return;
                }

                var sheetMap = new Dictionary<long, HashSet<long>>();
                using (var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                    foreach (ViewSheet sheet in sheets)
                    {
                        budget.Scan();
                        foreach (var view in sheet.GetAllPlacedViews())
                        {
                            var id = Id(view);
                            if (!sheetMap.ContainsKey(id))
                                sheetMap[id] = new HashSet<long>();
                            sheetMap[id].Add(Id(sheet.Id));
                        }
                    }

                using (var placements = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
                    foreach (ScheduleSheetInstance placement in placements)
                    {
                        budget.Scan();
                        var id = Id(placement.ScheduleId);
                        if (!sheetMap.ContainsKey(id))
                            sheetMap[id] = new HashSet<long>();
                        sheetMap[id].Add(Id(placement.OwnerViewId));
                    }

                var requested = new HashSet<long>(options.ViewIds);
                var visited = new HashSet<long>();
                int scanned = 0;
                using (var views = new FilteredElementCollector(doc).OfClass(typeof(View)))
                    foreach (View view in views)
                    {
                        budget.Scan();
                        var id = Id(view.Id);
                        if (view.IsTemplate || view is ViewSheet)
                            continue;
                        if (requested.Count > 0 && !requested.Contains(id))
                            continue;
                        if (options.ViewScope == "sheets" && !sheetMap.ContainsKey(id))
                            continue;
                        if (!FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id))
                        {
                            c.Partial("one_or_more_views_not_iterable");
                            continue;
                        }

                        visited.Add(id);
                        if (scanned >= options.MaxViews)
                        {
                            c.Partial("view_budget_exhausted");
                            break;
                        }

                        scanned++;
                        using (var elements = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                            foreach (var candidate in elements)
                            {
                                budget.Scan();
                                var target = Id(candidate.Id);
                                if (!targets.ContainsKey(target))
                                    continue;
                                Edge("presentation", target, view.Id, view is ViewSchedule ? "schedule_candidate_not_row" : "view_candidate_not_pixel_visibility", 1);
                                if (sheetMap.TryGetValue(id, out var sheetsForView))
                                    foreach (var sheet in sheetsForView)
                                        Edge("presentation", target, Eid(sheet), "sheet_with_candidate_view", 1);
                            }
                    }

                if (requested.Except(visited).Any())
                    c.Partial("requested_view_missing_excluded_or_not_scanned");
            });
            relations["presentation"].Skip("pixel_visibility_and_schedule_rows", "candidate_evidence_only");
        }

        void Proximity()
        {
            foreach (var pair in targets)
                RunCheck("proximity", "axis_aligned_bbox_candidates", pair.Key, c =>
                {
                    var box = pair.Value.get_BoundingBox(null);
                    if (box == null)
                    {
                        c.Partial("no_model_bounding_box");
                        return;
                    }

                    var points = new List<XYZ>();
                    foreach (var x in new[]
                    {
                        box.Min.X,
                        box.Max.X
                    }

                    )
                        foreach (var y in new[]
                        {
                            box.Min.Y,
                            box.Max.Y
                        }

                        )
                            foreach (var z in new[]
                            {
                                box.Min.Z,
                                box.Max.Z
                            }

                            )
                                points.Add(box.Transform.OfPoint(new XYZ(x, y, z)));
                    var pad = options.PaddingMm / 304.8;
                    var min = new XYZ(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points.Min(p => p.Z) - pad);
                    var max = new XYZ(points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad, points.Max(p => p.Z) + pad);
                    using (var outline = new Outline(min, max))
                    using (var filter = new BoundingBoxIntersectsFilter(outline))
                    using (var all = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
                        foreach (var e in all)
                        {
                            budget.Scan();
                            if (e is RevitLinkInstance)
                            {
                                c.Partial("linked_geometry_not_surveyed");
                                continue;
                            }

                            Edge("proximity", pair.Key, e.Id, "bbox_candidate_not_clash", 1);
                        }
                });
        }
    }
}
