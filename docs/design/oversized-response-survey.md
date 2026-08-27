# Khảo sát response quá khổ của MCP tools

> **Trạng thái:** GIAI ĐOẠN 1 — khảo sát tĩnh, chưa sửa code.
> **Phạm vi:** `rvt-mcp/src/server/Program.cs` và các handler tương ứng trong `src/shared/Handlers/`.
> **Inventory:** 232 `[McpServerTool]` (229 surface chuẩn + 3 adaptive-bake), 227 tool gọi plugin handler và 5 tool server-local. Hai tool tọa độ read-only thêm sau khảo sát gốc được phân loại bổ sung bên dưới.

## 1. Quy ước đo và ngưỡng

- Runtime xác định byte chính xác bằng `Encoding.UTF8.GetByteCount(exactSerializedPayload)`; khảo sát này chỉ ước lượng theo cardinality × kích thước DTO/item và độ sâu nested.
- Guard hiện tại đo JSON compact của plugin envelope. Final MCP text được server pretty-print nên không đồng nhất với byte count tại plugin boundary.
- 1 MiB JSON tương đương thô khoảng 200k–500k token tùy payload/tokenizer; với client-agnostic MCP, mức này có thể làm tràn context, kích hoạt truncate/compact và làm phiên tool-use không ổn định.

| Dải | Hành vi đã chốt |
|---|---|
| `<64 KiB` | Inline bình thường |
| `64 KiB ≤ size ≤ 256 KiB` | Inline + cảnh báo |
| `256 KiB < size ≤ 1 MiB` | Inline + cảnh báo mạnh + hướng dẫn thu hẹp |
| `>1 MiB` | Hard guard hiện tại; Bước 2 quyết định reject/summary/spill theo loại tool |

**Cách đọc ước lượng:** `Critical` không có nghĩa mọi lần gọi đều vượt 1 MiB; nó có nghĩa một request hợp lệ trên model lớn hoặc dữ liệu caller-controlled có đường thực tế vượt 1 MiB. Không dùng compressed size vì model nhận nội dung đã giải nén.

## 2. Bảng khảo sát

### Toolset `query`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_get_current_view_info` | `GetCurrentViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_get_selected_elements` | `GetSelectedElementsHandler.cs` | **Critical >1 MiB / unbounded** — UI selection không có trần và trả một DTO cho mỗi element được chọn. | Thiếu — cần `max_results` + `cursor` (hoặc chỉ `count`/ID preview) | **3** | Medium | B1: bổ sung `max_results` + `cursor` (hoặc chỉ `count`/ID preview). |
| `revit_get_available_family_types` | `GetFamilyTypesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `category` | **2** | Medium | B1: reject/warning hint trỏ đúng `category`; kiểm tra hard maximum. |
| `revit_ai_element_filter` | `AiElementFilterHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `parameterName`, `parameterValue`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `parameterName`, `parameterValue`, `limit`; kiểm tra hard maximum. |
| `revit_analyze_model_statistics` | `AnalyzeModelStatisticsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_get_material_quantities` | `GetMaterialQuantitiesHandler.cs` | **Critical >1 MiB / unbounded** — Một category có thể fan-out thành danh sách material không giới hạn; thiếu filter/cap kết quả. | Thiếu — cần `material_name_filter` + `max_results`/pagination | **3** | Medium | B1: bổ sung `material_name_filter` + `max_results`/pagination. |
| `revit_get_element_details` | `GetElementDetailsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`; kiểm tra hard maximum. |
| `revit_get_element_parameters` | `GetElementParametersHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `includeReadOnly` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `includeReadOnly`; kiểm tra hard maximum. |
| `revit_get_type_parameters` | `GetTypeParametersHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `typeIds` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `typeIds`; kiểm tra hard maximum. |
| `revit_list_project_parameters` | `ListProjectParametersHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `includeCategories` | **2** | Medium | B1: reject/warning hint trỏ đúng `includeCategories`; kiểm tra hard maximum. |
| `revit_get_element_relationships` | `GetElementRelationshipsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `includeDependents` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `includeDependents`; kiểm tra hard maximum. |
| `revit_list_groups` | `ListGroupsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `groupKind`, `includeMembers` | **2** | Medium | B1: reject/warning hint trỏ đúng `groupKind`, `includeMembers`; kiểm tra hard maximum. |
| `revit_get_group_members` | `GetGroupMembersHandler.cs` | **Critical >1 MiB / unbounded** — Một group có thể chứa danh sách member rất lớn; hiện chỉ có `groupId`. | Thiếu — cần `start_index` + `max_results` | **3** | Medium | B1: bổ sung `start_index` + `max_results`. |
| `revit_list_assemblies` | `ListAssembliesHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi assembly và member arrays tùy chọn mà không có limit. | Thiếu — cần `limit`/pagination và member preview cap | **3** | Medium | B1: bổ sung `limit`/pagination và member preview cap. |
| `revit_get_assembly_members` | `GetAssemblyMembersHandler.cs` | **Critical >1 MiB / unbounded** — Một assembly trả toàn bộ member DTO mà không có pagination. | Thiếu — cần `start_index` + `max_results` | **3** | Medium | B1: bổ sung `start_index` + `max_results`. |
| `revit_list_worksets` | `ListWorksetsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `schedule`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_schedules` | `ListSchedulesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `categoryFilter`, `namePattern` | **2** | Medium | B1: reject/warning hint trỏ đúng `categoryFilter`, `namePattern`; kiểm tra hard maximum. |
| `revit_get_schedule_definition` | `GetScheduleDefinitionHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `scheduleId` | **2** | Medium | B1: reject/warning hint trỏ đúng `scheduleId`; kiểm tra hard maximum. |
| `revit_get_schedule_data` | `GetScheduleDataHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `scheduleId`, `startRow`, `maxRows`, `includeCellMeta` | **2** | Medium | B1: reject/warning hint trỏ đúng `scheduleId`, `startRow`, `maxRows`, `includeCellMeta`; kiểm tra hard maximum. |
| `revit_get_schedule_formulas` | `GetScheduleFormulasHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `scheduleId` | **2** | Medium | B1: reject/warning hint trỏ đúng `scheduleId`; kiểm tra hard maximum. |
| `revit_get_schedulable_fields` | `GetSchedulableFieldsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `scheduleId`, `kindFilter` | **2** | Medium | B1: reject/warning hint trỏ đúng `scheduleId`, `kindFilter`; kiểm tra hard maximum. |
| `revit_find_schedule_elements` | `FindScheduleElementsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `scheduleId`, `includeParameters`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `scheduleId`, `includeParameters`, `limit`; kiểm tra hard maximum. |
| `revit_create_schedule` | `CreateScheduleHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_add_schedule_field` | `AddScheduleFieldHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_update_schedule_field` | `UpdateScheduleFieldHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_apply_schedule_filter_sort` | `ApplyScheduleFilterSortHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `families`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_loaded_families` | `ListLoadedFamiliesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `categoryFilter`, `kindFilter`, `includeInstanceCount`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `categoryFilter`, `kindFilter`, `includeInstanceCount`, `limit`; kiểm tra hard maximum. |
| `revit_load_family_from_path` | `LoadFamilyFromPathHandler.cs` | **Critical >1 MiB / unbounded** — Response mutation echo mọi symbol ID/name mới và tăng theo số family type. | Thiếu — cần `include_symbols=false` mặc định + `max_symbol_results` | **3** | Medium | B1: compact mutation response, giữ `success=true`; bổ sung `include_symbols=false` mặc định + `max_symbol_results`. |
| `revit_unload_family` | `UnloadFamilyHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_duplicate_family_type` | `DuplicateFamilyTypeHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_rename_family_type` | `RenameFamilyTypeHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_audit_families` | `AuditFamiliesHandler.cs` | **Critical >1 MiB / unbounded** — Trả toàn bộ finding của nhiều section mà không có cap theo section. | Thiếu — cần `limit_per_section` + pagination | **3** | Medium | B1: bổ sung `limit_per_section` + pagination. |
| `revit_replace_family_type` | `ReplaceFamilyTypeHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `fromTypeId`, `toTypeId`, `scope`, `viewId`, `dryRun` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `fromTypeId`, `toTypeId`, `scope`, `viewId`, `dryRun`; kiểm tra hard maximum. |
| `revit_get_family_instances` | `GetFamilyInstancesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `familyId`, `viewOnly`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `familyId`, `viewOnly`, `limit`; kiểm tra hard maximum. |
| `revit_list_family_types_in_family` | `ListFamilyTypesInFamilyHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi type và mọi type parameter trong một family mà không có cap. | Thiếu — cần `max_types`, `parameter_names`, pagination | **3** | Medium | B1: bổ sung `max_types`, `parameter_names`, pagination. |
| `revit_export_family_to_path` | `ExportFamilyToPathHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `create`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_create_line_based_element` | `CreateLineBasedElementHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_point_based_element` | `CreatePointBasedElementHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_surface_based_element` | `CreateSurfaceBasedElementHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_level` | `CreateLevelHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_grid` | `CreateGridHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_room` | `CreateRoomHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_group_from_elements` | `CreateGroupFromElementsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`; kiểm tra hard maximum. |

### Toolset `modify`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_operate_element` | `OperateElementHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_color_elements` | `ColorElementsHandler.cs` | **Critical >1 MiB / unbounded** — Mutation trả mọi nhóm parameter-value phân biệt mà không có result cap. | Thiếu — cần `max_groups` + compact mutation summary | **3** | Medium | B1: compact mutation response, giữ `success=true`; bổ sung `max_groups` + compact mutation summary. |
| `revit_set_element_parameter_values` | `SetElementParameterValuesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `parameterName` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `parameterName`; kiểm tra hard maximum. |
| `revit_set_type_parameter_values` | `SetTypeParameterValuesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `parameterName`, `typeIds`, `elementIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `parameterName`, `typeIds`, `elementIds`; kiểm tra hard maximum. |
| `revit_change_element_type` | `ChangeElementTypeHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `typeId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `typeId`; kiểm tra hard maximum. |
| `revit_assign_elements_to_workset` | `AssignElementsToWorksetHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `worksetId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `worksetId`; kiểm tra hard maximum. |

### Toolset `delete`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_delete_element` | `DeleteElementHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`; kiểm tra hard maximum. |

### Toolset `view`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_create_view` | `CreateViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_place_view_on_sheet` | `PlaceViewOnSheetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_analyze_sheet_layout` | `AnalyzeSheetLayoutHandler.cs` | **Critical >1 MiB / unbounded** — Một sheet trả mọi viewport; chưa có viewport pagination. | Thiếu — cần `start_viewport` + `max_viewports` | **3** | Medium | B1: bổ sung `start_viewport` + `max_viewports`. |
| `revit_capture_view_image` | `CaptureViewImageHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_view_crop` | `SetViewCropHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_view_scale` | `SetViewScaleHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_activate_view` | `ActivateViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_show_element_in_view` | `ShowElementInViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `export`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_export_room_data` | `ExportRoomDataHandler.cs` | **Critical >1 MiB / bulk** — Toàn bộ room dataset hiện không có scope và được trả inline. | Không/không đủ scope | **4** | High | B2: opt-in `output=file` (SQLite); trả schema + preview + lifecycle metadata. |
| `revit_export_pdf` | `ExportPdfHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewIds`; kiểm tra hard maximum. |
| `revit_export_dwg` | `ExportDwgHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewIds`; kiểm tra hard maximum. |
| `revit_export_dgn` | `ExportDgnHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewIds`; kiểm tra hard maximum. |
| `revit_export_dwf` | `ExportDwfHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewIds`; kiểm tra hard maximum. |
| `revit_export_ifc` | `ExportIfcHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_export_nwc` | `ExportNwcHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_export_fbx` | `ExportFbxHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_export_gbxml` | `ExportGbxmlHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_export_image` | `ExportImageHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_export_schedule_csv` | `ExportScheduleCsvHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_export_elements_data` | `ExportElementsDataHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_batch_export_sheets` | `BatchExportSheetsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `sheetIds`, `sheetNumberFilter` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `sheetIds`, `sheetNumberFilter`; kiểm tra hard maximum. |
| `revit_list_export_settings` | `ListExportSettingsHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi DWG/print/view-sheet setting mà không có filter hoặc pagination. | Thiếu — cần `kind_filter` + `limit`/pagination | **3** | Medium | B1: bổ sung `kind_filter` + `limit`/pagination. |
| `revit_create_view_sheet_set` | `CreateViewSheetSetHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewIds`; kiểm tra hard maximum. |
| `revit_get_print_settings` | `GetPrintSettingsHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi named print setting và sheet set mà không có pagination. | Thiếu — cần `kind_filter` + `limit`/pagination | **3** | Medium | B1: bổ sung `kind_filter` + `limit`/pagination. |

### Toolset `annotation`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_tag_all_walls` | `TagAllWallsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_tag_all_rooms` | `TagAllRoomsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_tag_elements` | `TagElementsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `viewId`, `tagTypeId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `viewId`, `tagTypeId`; kiểm tra hard maximum. |
| `revit_tag_all_by_category` | `TagAllByCategoryHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `viewId`, `tagTypeId`, `dryRun`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewId`, `tagTypeId`, `dryRun`, `limit`; kiểm tra hard maximum. |
| `revit_create_text_note` | `CreateTextNoteHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_dimensions` | `CreateDimensionsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `references`, `viewId`, `dimensionTypeId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `references`, `viewId`, `dimensionTypeId`; kiểm tra hard maximum. |
| `revit_create_filled_region` | `CreateFilledRegionHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_detail_line` | `CreateDetailLineHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_callout_view` | `CreateCalloutViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_keynotes` | `ListKeynotesHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `limit`; kiểm tra hard maximum. |
| `revit_apply_keynote_to_element` | `ApplyKeynoteToElementHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `dryRun` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `dryRun`; kiểm tra hard maximum. |
| `revit_find_untagged_elements` | `FindUntaggedElementsHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `viewId`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `viewId`, `limit`; kiểm tra hard maximum. |
| `revit_find_undimensioned_elements` | `FindUndimensionedElementsHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `viewId`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `viewId`, `limit`; kiểm tra hard maximum. |
| `revit_wipe_empty_tags` | `WipeEmptyTagsHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `viewId`, `dryRun`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `viewId`, `dryRun`, `limit`; kiểm tra hard maximum. |

### Toolset `mep`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_detect_system_elements` | `DetectSystemElementsHandler.cs` | **Critical >1 MiB / unbounded** — Connector traversal trả mọi connected element ID theo category mà không có cap. | Thiếu — cần `max_elements` + pagination/summary by category | **3** | Medium | B1: bổ sung `max_elements` + pagination/summary by category. |
| `revit_create_duct` | `CreateDuctHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_pipe` | `CreatePipeHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_cable_tray` | `CreateCableTrayHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_conduit` | `CreateConduitHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_air_terminal` | `CreateAirTerminalHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_lighting_fixture` | `CreateLightingFixtureHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_mep_systems` | `ListMepSystemsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `domainFilter`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `domainFilter`, `limit`; kiểm tra hard maximum. |
| `revit_get_system_inventory` | `GetSystemInventoryHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `systemId`, `includeParameters`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `systemId`, `includeParameters`, `limit`; kiểm tra hard maximum. |
| `revit_get_mep_element_connectors` | `GetMepElementConnectorsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_connect_mep_elements` | `ConnectMepElementsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_mep_fitting` | `CreateMepFittingHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_system_classification` | `SetSystemClassificationHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `systemId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `systemId`; kiểm tra hard maximum. |
| `revit_get_panel_schedule` | `GetPanelScheduleHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi circuit của panel mà không có circuit pagination. | Thiếu — cần `start_circuit` + `max_circuits` | **3** | Medium | B1: bổ sung `start_circuit` + `max_circuits`. |
| `revit_find_mep_disconnects` | `FindMepDisconnectsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `domainFilter`, `viewOnly`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `domainFilter`, `viewOnly`, `limit`; kiểm tra hard maximum. |
| `revit_analyze_mep_network` | `AnalyzeMepNetworkHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `graphics`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_create_view_filter` | `CreateViewFilterHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `categories` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `categories`; kiểm tra hard maximum. |
| `revit_apply_filter_to_view` | `ApplyFilterToViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_filter_overrides` | `SetFilterOverridesHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_view_filters` | `ListViewFiltersHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewId`, `includeUsage` | **2** | Medium | B1: reject/warning hint trỏ đúng `viewId`, `includeUsage`; kiểm tra hard maximum. |
| `revit_remove_filter_from_view` | `RemoveFilterFromViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_override_element_graphics` | `OverrideElementGraphicsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `viewId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `viewId`; kiểm tra hard maximum. |
| `revit_clear_element_overrides` | `ClearElementOverridesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `viewId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `viewId`; kiểm tra hard maximum. |
| `revit_get_view_visibility` | `GetViewVisibilityHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `viewId`, `includeCategoryList` | **2** | Medium | B1: reject/warning hint trỏ đúng `viewId`, `includeCategoryList`; kiểm tra hard maximum. |
| `revit_set_category_visibility` | `SetCategoryVisibilityHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `categories`, `hidden`, `viewId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `categories`, `hidden`, `viewId`; kiểm tra hard maximum. |
| `revit_list_phases` | `ListPhasesHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_view_phase` | `SetViewPhaseHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_element_phase` | `SetElementPhaseHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `phaseCreatedId`, `phaseCreatedName`, `phaseDemolishedId`, `phaseDemolishedName` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `phaseCreatedId`, `phaseCreatedName`, `phaseDemolishedId`, `phaseDemolishedName`; kiểm tra hard maximum. |

### Toolset `meta`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_send_code_to_revit` | `SendCodeToRevitHandler.cs` | **Critical >1 MiB / bất định** — Giá trị trả về từ C# tùy ý; không thể kiểm soát schema hoặc cardinality. | Không thể scope centrally; agent tự viết code | **Riêng** | High | B2/§6: auto-spill theo size; path + byte_count + preview; không reject cứng. |
| `revit_show_message` | `ShowMessageHandler.cs` | **Critical >1 MiB / unbounded** — Tool echo text do caller kiểm soát sau khi đã hiển thị dialog. | Thiếu — cần không echo toàn bộ message; `echo_message=false`/`max_echo_chars` | **3** | Medium | B1: bổ sung không echo toàn bộ message; `echo_message=false`/`max_echo_chars`. |
| `revit_list_available_targets` | `Program.cs (server-local)` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_get_current_target` | `Program.cs (server-local)` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_switch_target` | `Program.cs + GetCurrentViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_batch_execute` | `BatchExecuteHandler.cs` | **Critical >1 MiB / bulk** — Tối đa 20 sub-result khác schema; detail mutation có thể cộng dồn khó đoán. | Có một phần — `commands`, `continueOnError` | **4** | High | B1: mutation trả compact summary, giữ `success=true`. B2: opt-in `output=file` (NDJSON/JSON); trả schema + preview + lifecycle metadata. |
| `revit_set_project_info` | `SetProjectInfoHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_purge_unused` | `PurgeUnusedHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `targets`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `targets`, `limit`; kiểm tra hard maximum. |
| `revit_analyze_usage_patterns` | `Program.cs (server-local)` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `toolbaker`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_baked_tools` | `ListBakedToolsHandler.cs` | **Critical >1 MiB / unbounded** — Mọi registry entry chứa description và full parameter schema; count/text không có trần. | Thiếu — cần `name_filter` + `limit`/cursor | **3** | Medium | B1: bổ sung `name_filter` + `limit`/cursor. |
| `revit_run_baked_tool` | `RunBakedToolHandler.cs` | **Critical >1 MiB / bulk** — Output của baked command phụ thuộc code người dùng và không thể đặt trần tập trung. | Có một phần — `name`, `params` | **4** | High | B1: mutation trả compact summary, giữ `success=true`. B2: opt-in `output=file` (NDJSON/JSON/text); trả schema + preview + lifecycle metadata. |
| `revit_list_bake_suggestions` | `src/server/Handlers/ListBakeSuggestionsHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi suggestion chưa archived; SQLite query không có limit. | Thiếu — cần `state` + `limit`/cursor | **3** | Medium | B1: bổ sung `state` + `limit`/cursor. |
| `revit_accept_bake_suggestion` | `AcceptBakeSuggestionHandler.cs + ApplyBakeSuggestionHandler.cs` | **Critical >1 MiB / unbounded** — Response thành công echo source code và DLL base64 sau khi tool đã được register. | Thiếu — cần bỏ `source_code`/`dll_base64`; trả hash + byte count | **3** | Medium | B1: compact mutation response, giữ `success=true`; bổ sung bỏ `source_code`/`dll_base64`; trả hash + byte count. |
| `revit_dismiss_bake_suggestion` | `src/server/Handlers/DismissBakeSuggestionHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `structural`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_create_structural_column` | `CreateStructuralColumnHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_structural_beam` | `CreateStructuralBeamHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_structural_wall` | `CreateStructuralWallHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_foundation_isolated` | `CreateFoundationIsolatedHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_foundation_wall` | `CreateFoundationWallHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_rebar` | `ListRebarHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `host_id`, `view_id`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `host_id`, `view_id`, `limit`; kiểm tra hard maximum. |
| `revit_get_structural_loads` | `GetStructuralLoadsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `element_id`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `element_id`, `limit`; kiểm tra hard maximum. |
| `revit_set_structural_load` | `SetStructuralLoadHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_analyze_structural_connections` | `AnalyzeStructuralConnectionsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `element_ids`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `element_ids`, `limit`; kiểm tra hard maximum. |
| `revit_tag_structural_framing` | `TagStructuralFramingHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_rebar_set` | `CreateRebarSetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_rebar_stirrup` | `CreateRebarStirrupHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `lint`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_analyze_view_naming_patterns` | `AnalyzeViewNamingPatternsHandler.cs` | **Critical >1 MiB / unbounded** — Trả mọi naming outlier; số view của project không có trần. | Thiếu — cần `max_outliers` + cursor | **3** | Medium | B1: bổ sung `max_outliers` + cursor. |
| `revit_suggest_view_name_corrections` | `SuggestViewNameCorrectionsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_detect_firm_profile` | `DetectFirmProfileHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_get_model_warnings_summary` | `GetModelWarningsSummaryHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `include_examples`, `max_examples_per_type` | **2** | Medium | B1: reject/warning hint trỏ đúng `include_examples`, `max_examples_per_type`; kiểm tra hard maximum. |

### Toolset `sheets`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_create_sheet` | `CreateSheetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_duplicate_sheet` | `DuplicateSheetHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `sourceSheetId`, `includeSchedules`, `includeRevisions` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `sourceSheetId`, `includeSchedules`, `includeRevisions`; kiểm tra hard maximum. |
| `revit_create_placeholder_sheet` | `CreatePlaceholderSheetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_sheets` | `ListSheetsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `numberFilter`, `namePattern`, `includeRevisions`, `includeViewports`, `includePlaceholders`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `numberFilter`, `namePattern`, `includeRevisions`, `includeViewports`, `includePlaceholders`, `limit`; kiểm tra hard maximum. |
| `revit_set_titleblock_parameters` | `SetTitleblockParametersHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `parameters`, `sheetId`, `target` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `parameters`, `sheetId`, `target`; kiểm tra hard maximum. |
| `revit_get_titleblock_parameters` | `GetTitleblockParametersHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `sheetId`, `target`, `includeReadOnly` | **2** | Medium | B1: reject/warning hint trỏ đúng `sheetId`, `target`, `includeReadOnly`; kiểm tra hard maximum. |
| `revit_list_titleblocks` | `ListTitleblocksHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `namePattern`, `includeInactive`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `namePattern`, `includeInactive`, `limit`; kiểm tra hard maximum. |
| `revit_place_schedule_on_sheet` | `PlaceScheduleOnSheetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_revision` | `CreateRevisionHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_assign_revision_to_sheet` | `AssignRevisionToSheetHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `revisionId`, `sheetIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `revisionId`, `sheetIds`; kiểm tra hard maximum. |
| `revit_list_revisions` | `ListRevisionsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `includeSheets` | **2** | Medium | B1: reject/warning hint trỏ đúng `includeSheets`; kiểm tra hard maximum. |
| `revit_renumber_sheets` | `RenumberSheetsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `items`, `dryRun` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `items`, `dryRun`; kiểm tra hard maximum. |

### Toolset `materials`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_materials` | `ListMaterialsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `namePattern`, `classFilter`, `includeAssets`, `includeUseCount`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `namePattern`, `classFilter`, `includeAssets`, `includeUseCount`, `limit`; kiểm tra hard maximum. |
| `revit_get_material_properties` | `GetMaterialPropertiesHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `materialId`, `includeAssets`, `includeParameters` | **2** | Medium | B1: reject/warning hint trỏ đúng `materialId`, `includeAssets`, `includeParameters`; kiểm tra hard maximum. |
| `revit_create_material` | `CreateMaterialHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_duplicate_material` | `DuplicateMaterialHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_material_appearance` | `SetMaterialAppearanceHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_material_identity` | `SetMaterialIdentityHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_material_structural_asset` | `SetMaterialStructuralAssetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_material_thermal_asset` | `SetMaterialThermalAssetHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_assign_material_to_element` | `AssignMaterialToElementHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `materialId`, `parameterName` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `materialId`, `parameterName`; kiểm tra hard maximum. |
| `revit_get_material_takeoff` | `GetMaterialTakeoffHandler.cs` | **Critical >1 MiB / bulk** — Takeoff toàn model theo material/category/element là dữ liệu bulk dạng quan hệ. | Có một phần — `categoryFilter`, `materialNamePattern`, `includeElements`, `elementLimit` | **4** | High | B2: opt-in `output=file` (SQLite); trả schema + preview + lifecycle metadata. |

### Toolset `geometry`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_get_element_bounding_box` | `GetElementBoundingBoxHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `elementIds`, `viewId`, `includeTransform` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `viewId`, `includeTransform`; kiểm tra hard maximum. |
| `revit_get_element_geometry` | `GetElementGeometryHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `elementIds`, `detailLevel`, `includeSamples`, `sampleLimit` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `detailLevel`, `includeSamples`, `sampleLimit`; kiểm tra hard maximum. |
| `revit_measure_distance_between_elements` | `MeasureDistanceBetweenElementsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_clash_detection` | `ClashDetectionHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `categoriesA`, `categoriesB`, `viewId`, `maxPairs`, `maxResults` | **2** | Medium | B1: reject/warning hint trỏ đúng `categoriesA`, `categoriesB`, `viewId`, `maxPairs`, `maxResults`; kiểm tra hard maximum. |
| `revit_raycast_from_point` | `RaycastFromPointHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_find_elements_in_volume` | `FindElementsInVolumeHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `roomId`, `categories`, `viewId`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `roomId`, `categories`, `viewId`, `limit`; kiểm tra hard maximum. |
| `revit_compute_element_volume` | `ComputeElementVolumeHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `elementIds`, `detailLevel` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `detailLevel`; kiểm tra hard maximum. |
| `revit_compute_element_area` | `ComputeElementAreaHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `elementIds`, `detailLevel` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `detailLevel`; kiểm tra hard maximum. |
| `revit_project_point_onto_face` | `ProjectPointOntoFaceHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_find_overlapping_elements` | `FindOverlappingElementsHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `viewId`, `maxPairs`, `maxResults` | **2** | Medium | B1: reject/warning hint trỏ đúng `viewId`, `maxPairs`, `maxResults`; kiểm tra hard maximum. |
| `revit_get_element_centroid` | `GetElementCentroidHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `elementIds` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`; kiểm tra hard maximum. |
| `revit_analyze_geometry_complexity` | `AnalyzeGeometryComplexityHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `elementIds`, `categories`, `viewId`, `detailLevel`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `elementIds`, `categories`, `viewId`, `detailLevel`, `limit`; kiểm tra hard maximum. |

### Toolset `rooms`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_rooms` | `ListRoomsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `levelName`, `phaseName`, `status`, `includeParameters`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `levelName`, `phaseName`, `status`, `includeParameters`, `limit`; kiểm tra hard maximum. |
| `revit_get_room_boundaries` | `GetRoomBoundariesHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `roomId`, `includeBoundaryElements` | **2** | Medium | B1: reject/warning hint trỏ đúng `roomId`, `includeBoundaryElements`; kiểm tra hard maximum. |
| `revit_get_room_openings` | `GetRoomOpeningsHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `roomId`, `includeDoors`, `includeWindows` | **2** | Medium | B1: reject/warning hint trỏ đúng `roomId`, `includeDoors`, `includeWindows`; kiểm tra hard maximum. |
| `revit_create_room_separator` | `CreateRoomSeparatorHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_area` | `CreateAreaHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_create_space` | `CreateSpaceHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_areas` | `ListAreasHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `levelName`, `status`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `levelName`, `status`, `limit`; kiểm tra hard maximum. |
| `revit_compute_room_finishes` | `ComputeRoomFinishesHandler.cs` | **Critical >1 MiB / bulk** — Hàng nghìn room cùng finish/material summary tạo thành dataset quan hệ lớn. | Có một phần — `roomIds`, `levelName`, `includeEmpty`, `limit` | **4** | High | B2: opt-in `output=file` (SQLite); trả schema + preview + lifecycle metadata. |
| `revit_auto_create_rooms_from_walls` | `AutoCreateRoomsFromWallsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `levelName`, `phaseName`, `dryRun`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `levelName`, `phaseName`, `dryRun`, `limit`; kiểm tra hard maximum. |
| `revit_tag_all_areas` | `TagAllAreasHandler.cs` | **Critical >1 MiB / unbounded** — Mọi area trong plan tạo per-item mutation report mà không có limit. | Thiếu — cần `limit` + compact mutation summary | **3** | Medium | B1: compact mutation response, giữ `success=true`; bổ sung `limit` + compact mutation summary. |

### Toolset `links`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_linked_models` | `ListLinkedModelsHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_linked_cad` | `ListLinkedCadHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_import_cad_to_view` | `ImportCadToViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_link_revit_model` | `LinkRevitModelHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_unload_link` | `UnloadLinkHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_reload_link` | `ReloadLinkHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_get_link_elements` | `GetLinkElementsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `linkInstanceId`, `limit`, `includeBoundingBox` | **2** | Medium | B1: reject/warning hint trỏ đúng `linkInstanceId`, `limit`, `includeBoundingBox`; kiểm tra hard maximum. |
| `revit_get_project_coordinate_system` | `GetProjectCoordinateSystemHandler.cs` | **Low <64 KiB** — Chỉ trả các origin và danh sách Project Location, không quét model elements. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_get_link_coordinate_system` | `GetLinkCoordinateSystemHandler.cs` | **Low <64 KiB** — Một link, transform, origin mapping và Project Locations của linked document. | Có — `linkInstanceId` | **1** | High | Không đụng; giữ inline. |
| `revit_acquire_coordinates_from_link` | `AcquireCoordinatesFromLinkHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_publish_coordinates_to_link` | `PublishCoordinatesToLinkHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_set_project_base_point` | `SetProjectBasePointHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |

### Toolset `parameters`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_shared_parameters` | `ListSharedParametersHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `sharedParameterFilePath`, `includeBindings`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `sharedParameterFilePath`, `includeBindings`, `limit`; kiểm tra hard maximum. |
| `revit_create_shared_parameter` | `CreateSharedParameterHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_bind_shared_parameter` | `BindSharedParameterHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `guid`, `categories`, `bindingKind`, `parameterGroupId`, `sharedParameterFilePath` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `guid`, `categories`, `bindingKind`, `parameterGroupId`, `sharedParameterFilePath`; kiểm tra hard maximum. |
| `revit_create_project_parameter` | `CreateProjectParameterHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_list_project_parameter_bindings` | `ListProjectParameterBindingsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `includeCategories`, `includeShared`, `includeProject`, `nameFilter`, `guid`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `includeCategories`, `includeShared`, `includeProject`, `nameFilter`, `guid`, `limit`; kiểm tra hard maximum. |
| `revit_remove_parameter_binding` | `RemoveParameterBindingHandler.cs` | **Medium 64–256 KiB** — Collection đã bounded/scoped vẫn có thể đạt hàng trăm DTO compact. | Có — `guid`, `categories`, `removeAllCategories`, `dryRun` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `guid`, `categories`, `removeAllCategories`, `dryRun`; kiểm tra hard maximum. |
| `revit_export_shared_parameter_file` | `ExportSharedParameterFileHandler.cs` | **Critical >1 MiB / bulk** — Toàn bộ groups/definitions/bindings lồng nhau (và raw lines tùy chọn) là dữ liệu bulk. | Có một phần — `sharedParameterFilePath` | **4** | High | B2: opt-in `output=file` (JSON/NDJSON); trả schema + preview + lifecycle metadata. |
| `revit_set_parameter_value_by_guid` | `SetParameterValueByGuidHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `guid`, `target` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `guid`, `target`; kiểm tra hard maximum. |

### Toolset `organization`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_list_view_templates` | `ListViewTemplatesHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `viewId`, `includeSettings`, `includeUsage`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `viewId`, `includeSettings`, `includeUsage`, `limit`; kiểm tra hard maximum. |
| `revit_create_view_template_from_view` | `CreateViewTemplateFromViewHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_apply_view_template` | `ApplyViewTemplateHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `templateId`, `viewIds` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `templateId`, `viewIds`; kiểm tra hard maximum. |
| `revit_duplicate_view_template` | `DuplicateViewTemplateHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_delete_view_template` | `DeleteViewTemplateHandler.cs` | **Critical >1 MiB / unbounded** — Một template có thể trả mọi dependent view và deleted ID sau mutation. | Thiếu — cần `max_used_by_views` + compact mutation summary | **3** | Medium | B1: compact mutation response, giữ `success=true`; bổ sung `max_used_by_views` + compact mutation summary. |
| `revit_save_selection` | `SaveSelectionHandler.cs` | **Critical >1 MiB / unbounded** — Response mutation trả toàn bộ saved ID list hoặc active UI selection. | Thiếu — cần `include_element_ids=false` + ID preview cap | **3** | Medium | B1: compact mutation response, giữ `success=true`; bổ sung `include_element_ids=false` + ID preview cap. |
| `revit_load_selection` | `LoadSelectionHandler.cs` | **Critical >1 MiB / unbounded** — Một saved selection trả mọi ID và optional element DTO mà không có pagination. | Thiếu — cần `start_index` + `max_results` + summary flag | **3** | Medium | B1: bổ sung `start_index` + `max_results` + summary flag. |
| `revit_list_saved_selections` | `ListSavedSelectionsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `nameFilter`, `includeElementIds`, `includeElementSummary`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `nameFilter`, `includeElementIds`, `includeElementSummary`, `limit`; kiểm tra hard maximum. |
| `revit_delete_saved_selection` | `DeleteSavedSelectionHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_select_elements` | `SelectElementsHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `elementIds`, `savedSelectionId` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `elementIds`, `savedSelectionId`; kiểm tra hard maximum. |

### Toolset `workflows`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_workflow_clash_review` | `WorkflowClashReviewHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `view_id`, `max_pairs` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `view_id`, `max_pairs`; kiểm tra hard maximum. |
| `revit_workflow_model_audit` | `WorkflowModelAuditHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `include_warnings`, `include_families`, `include_views`, `include_schedules`, `include_mep`, `limit_per_section` | **2** | Medium | B1: reject/warning hint trỏ đúng `include_warnings`, `include_families`, `include_views`, `include_schedules`, `include_mep`, `limit_per_section`; kiểm tra hard maximum. |
| `revit_workflow_room_documentation` | `WorkflowRoomDocumentationHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `room_ids`, `level_name`, `tag_rooms`, `sheet_id`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `room_ids`, `level_name`, `tag_rooms`, `sheet_id`, `limit`; kiểm tra hard maximum. |
| `revit_workflow_sheet_set` | `WorkflowSheetSetHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `sheets`, `renumber_strategy` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `sheets`, `renumber_strategy`; kiểm tra hard maximum. |
| `revit_workflow_data_roundtrip` | `WorkflowDataRoundtripHandler.cs` | **Critical >1 MiB / bulk** — Validation import/export và báo cáo thay đổi từng row có thể lớn tùy ý. | Có một phần — `parameter_names` | **4** | High | B1: mutation trả compact summary, giữ `success=true`. B2: opt-in `output=file` (NDJSON/JSON); trả schema + preview + lifecycle metadata. |
| `revit_workflow_view_cleanup` | `WorkflowViewCleanupHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `include_unused_views`, `include_empty_schedules`, `include_naming_outliers`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `include_unused_views`, `include_empty_schedules`, `include_naming_outliers`, `limit`; kiểm tra hard maximum. |
| `revit_workflow_naming_normalization` | `WorkflowNamingNormalizationHandler.cs` | **High >256 KiB–1 MiB** — Scope có thể trả hàng trăm record nested/per-item, hợp lý để vượt 256 KiB. | Có — `target`, `pattern`, `ids`, `limit` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `target`, `pattern`, `ids`, `limit`; kiểm tra hard maximum. |
| `revit_workflow_takeoff_report` | `WorkflowTakeoffReportHandler.cs` | **Critical >1 MiB / bulk** — Báo cáo element/material/quantity nhiều category có bản chất bulk. | Có một phần — `categories`, `include_materials`, `include_quantities`, `include_cost`, `limit_per_category` | **4** | High | B1: mutation trả compact summary, giữ `success=true`. B2: opt-in `output=file` (SQLite); trả schema + preview + lifecycle metadata. |

### Toolset `kei`

| Tool (revit_*) | Handler file | Ước lượng size rủi ro | Đã có scope? | Nhóm | Tin cậy | Đề xuất hành động |
|---|---|---|---|---:|---|---|
| `revit_get_active_project_db` | `GetActiveProjectDbHandler.cs` | **Low <64 KiB** — DTO fixed/single-target hoặc aggregate compact; không trả model-wide detail list. | N/A — output compact | **1** | High | Không đụng; giữ inline. |
| `revit_query_kei_database` | `QueryKeiDatabaseHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `preset`, `sql`, `database`, `limit` | **2** | Medium | B1: reject/warning hint trỏ đúng `preset`, `sql`, `database`, `limit`; kiểm tra hard maximum. |
| `revit_write_kei_database` | `WriteKeiDatabaseHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `sql`, `statements`, `dryRun`, `database` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `sql`, `statements`, `dryRun`, `database`; kiểm tra hard maximum. |
| `revit_import_project_equipment` | `ImportProjectEquipmentHandler.cs` | **Critical >1 MiB khả dĩ** — Caller cap cao, row nested giàu dữ liệu hoặc explicit ID list lớn có thể vượt 1 MiB. | Có — `items`, `dryRun`, `database` | **2** | Medium | B1: compact mutation response/detail, giữ `success=true`; reject/warning hint trỏ đúng `items`, `dryRun`, `database`; kiểm tra hard maximum. |

## 3. Tổng hợp

### Theo nhóm hành động

| Nhóm | Số tool |
|---|---:|
| 1 — An toàn/compact | 103 |
| 2 — Rủi ro, đã có scope | 97 |
| 3 — Rủi ro, thiếu scope | 23 |
| 4 — Bulk/bất định | 8 |
| Riêng — `send_code` | 1 |
| **Tổng** | **232** |

### Theo dải ước lượng

| Dải | Số tool |
|---|---:|
| Low `<64 KiB` | 103 |
| Medium `64–256 KiB` | 16 |
| High `>256 KiB–1 MiB` | 13 |
| Critical `>1 MiB`/unbounded/bulk | 100 |

### Ứng viên Bước 2 — nhóm 4

| Tool | Format đề xuất | Lý do |
|---|---|---|
| `revit_batch_execute` | NDJSON/JSON | Tối đa 20 sub-result khác schema; detail mutation có thể cộng dồn khó đoán. |
| `revit_compute_room_finishes` | SQLite | Hàng nghìn room cùng finish/material summary tạo thành dataset quan hệ lớn. |
| `revit_export_room_data` | SQLite | Toàn bộ room dataset hiện không có scope và được trả inline. |
| `revit_export_shared_parameter_file` | JSON/NDJSON | Toàn bộ groups/definitions/bindings lồng nhau (và raw lines tùy chọn) là dữ liệu bulk. |
| `revit_get_material_takeoff` | SQLite | Takeoff toàn model theo material/category/element là dữ liệu bulk dạng quan hệ. |
| `revit_run_baked_tool` | NDJSON/JSON/text | Output của baked command phụ thuộc code người dùng và không thể đặt trần tập trung. |
| `revit_workflow_data_roundtrip` | NDJSON/JSON | Validation import/export và báo cáo thay đổi từng row có thể lớn tùy ý. |
| `revit_workflow_takeoff_report` | SQLite | Báo cáo element/material/quantity nhiều category có bản chất bulk. |

### Trường hợp riêng

- `revit_send_code_to_revit`: auto-spill khi output vượt ngưỡng; dùng NDJSON/JSON/text tùy giá trị thực, trả `path`, `byte_count`, preview và hướng dẫn phân tích. Không biến execution đã chạy thành `success=false`.

## 4. Quan sát ảnh hưởng Bước 1

- Các mutation có response per-item xuất hiện nhiều ở nhóm 2/3. Chúng phải trả count/ID summary và giữ `success=true` sau khi transaction/side effect đã hoàn tất; detail có thể truncate hoặc spill.
- Một số `limit` chỉ có default nhưng không có hard maximum (`ai_element_filter`, structural list/audit tools, KEI query). Bước 1 phải clamp/validate chứ không chỉ nhắc lại tên tham số.
- Cảnh báo 64/256 KiB phải dựa trên byte count thực tế. Nếu mục tiêu là giúp agent đổi chiến lược, cảnh báo cần agent-visible; stderr-only chỉ có giá trị observability.
- Nên ghi cả `plugin_wire_bytes` và `mcp_text_bytes` trong test/telemetry không chứa payload; final context policy nên căn theo `mcp_text_bytes`.

## 5. Chốt dừng #1

Khảo sát hoàn tất. **Không có product code nào được sửa.** Chờ User + Claude duyệt phân nhóm, ứng viên spill và ngưỡng trước khi sang Giai đoạn 2/Bước 1.
