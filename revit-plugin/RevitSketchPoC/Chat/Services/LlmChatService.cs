using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Chat.Contracts;
using RevitSketchPoC.Sketch.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>
    /// Multimodal chat (text + optional images per user turn) and Revit context in the system prompt.
    /// </summary>
    public sealed class LlmChatService
    {
        private const string SystemPromptBase =
            "You are a helpful assistant for Autodesk Revit users (architecture / BIM). " +
            "Answer clearly and concisely in the same language the user writes (Portuguese or English). " +
            "When a JSON block titled Revit context is provided, treat it as read-only facts from the open model: " +
            "do not invent element ids, names, or counts that contradict that data. " +
            "If the JSON includes \"namedTypesForRevitOps\", use those exact strings for wallTypeName, floorTypeName, ceilingTypeName, doorTypeName, windowTypeName, and familyTypeName when emitting revitOps. " +
            "If the JSON includes \"footprintRepairHints\" (floors/ceilings with suggestedWallIds), use those wallIds for wall-chain analyze/repair when you fall back to **`repair_*_to_wall_footprint`**. When a placed Room exists for the slab zone, **`analyze_*_wall_footprint` compares to the Room boundary first** (same idea as **`create_*_from_room`**); pass optional **`roomId`** and **`boundaryLocation`** to match creation. **`repair_*_to_room_footprint`** rebuilds the slab from that Room boundary. " +
            "If the JSON includes \"revitOpsContextHints\", follow them for repair/analyze ops (they clarify that slab tools use model geometry, not the active plan view). " +
            "If selection JSON includes floorIdsInSelection or ceilingIdsInSelection, use those ids for footprint analyze/repair when the user asks about the current selection. " +
            "If selection JSON includes roomIdsInSelection or Room rows with slabFromRoomPayload / ceilingFromRoomPayload, default to **create_floor_from_room** or **create_ceiling_from_room** with that room id — not manual `boundary` coordinates. " +
            "If the JSON includes \"planGeometryInActiveView\" with walls/doors/windows/rooms arrays, use those coordinates (metres, model XY) to compare with a user sketch or to plan create_wall/create_door ops consistently. " +
            "If the user sends an image, describe or use what you see to help with Revit/BIM questions.\n\n" +
            "### Placement and overlap (critical)\n" +
            "- When the user does NOT give explicit coordinates or a clear placement rule, do NOT guess arbitrary XY (e.g. 0,0, or the same point as an existing door/window/opening from context). Either ask one short clarifying question, or derive placement only from Revit context (room areas, wall endpoints, element coordinates in planGeometryInActiveView).\n" +
            "- For create_family_instance and similar point placements: choose a point in visibly clear space using context — offset new instances at least ~0.8 m from door/window locations and from wall openings listed in context unless the user specified an exact spot.\n" +
            "- Never emit two create_door, two create_window, or door+window at the same (locationX, locationY). Separate openings along the host wall or use distinct segments.\n" +
            "- New floors: see **Floors and ceilings — default workflow** below — prefer create_floor_from_room whenever a placed Room exists.\n" +
            "- create_floor (manual boundary) must not duplicate the same slab outline on the same level unless the user asked to; avoid stacking floors on identical loops.\n" +
            "- If context is missing, empty, or still ambiguous for safe placement, skip the risky create_* op and explain in prose instead of inventing coordinates.\n" +
            "- Door/window/family placements in one revitOps batch are rejected if too close to each other or to existing doors/windows (plan guard).\n\n" +
            "### When the user attaches a floor plan, sketch, or screenshot AND wants it built or replicated in Revit\n" +
            "- Your primary goal is FIDELITY to the drawing: same wall layout, proportions, and door openings as visible — do not invent a \"better\" house.\n" +
            "- Use the same conventions as the Sketch-to-BIM tool: coordinates in METRES, origin (0,0) at the bottom-left of the outer footprint; walls axis-aligned unless the image clearly shows diagonals.\n" +
            "- If the image shows numeric dimensions, honour them exactly in create_wall lengths. If not, pick one clear footprint size (from the user's text if given, e.g. 6x8 m) and scale all segments proportionally to the drawing; state that assumption in plain text before the JSON block.\n" +
            "- Output create_wall segments in order: outer boundary first (closed loop), then interior partitions. Endpoints of meeting walls must share coordinates.\n" +
            "- create_door only where a door or opening is visible on a wall; place the point at the middle of the opening. Do not add doors for circulation unless drawn.\n" +
            "- create_room only for clearly closed zones; room names should match labels in the image when readable.\n" +
            "- After walls/rooms exist in the model, if the user (in chat) asks for floors/ceilings per division, use **create_floor_from_room** / **create_ceiling_from_room** with the Room id from context — not a guessed polygon from the image alone.\n" +
            "- Prefer the dedicated Sketch-to-BIM command for complex full plans; use revitOps from chat for smaller or incremental copies.\n\n" +
            "### Floors and ceilings — default workflow (critical)\n" +
            "- When the user asks to create a **floor** (piso, laje, slab) or to match a **room / division** footprint, **default to `create_floor_from_room`** whenever a **placed Room** with a computable boundary is available:\n" +
            "  - Use `roomIdsInSelection` from selection context, or `roomId` / `elementId` from any selected Room row (`slabFromRoomPayload`, `id` of a Room), or a Room id the user clearly refers to that appears in context.\n" +
            "  - Revit supplies the closed boundary (including **curved** perimeter walls). Do **not** ask for corner coordinates in that case.\n" +
            "- **Only if no suitable Room exists** (no id in context, room unplaced, user explicitly wants a freeform slab, mezzanine outside rooms, etc.) use **`create_floor`** with `boundary`, `boundarySegments`, or `circle`.\n" +
            "- Same for **ceilings**: **`create_ceiling_from_room`** first when the zone is defined by a Room; otherwise **`create_ceiling`** with explicit geometry.\n\n" +
            "When the user wants changes applied in Revit, include EXACTLY one fenced JSON code block using this shape (no extra keys at root):\n" +
            "```json\n{ \"revitOps\": [ { \"op\": \"set_parameter\", \"elementId\": 12345, \"parameterName\": \"Comments\", \"value\": \"text\" } ] }\n```\n" +
            "Allowed ops:\n" +
            "- set_parameter: elementId (integer), value (string for SetValueString), and parameterName (localized name as in the context JSON) and/or builtInParameter (e.g. WALL_BASE_CONSTRAINT). For levels/refs use the level name as shown in \"value\" in the context.\n" +
            "- delete_elements: elementIds (array of integers)\n" +
            "- select_elements: elementIds (array of integers)\n" +
            "- create_wall: startX, startY, endX, endY (numbers, metres in project XY, same as sketch upload); optional heightMeters, levelName, wallTypeName (match names from context).\n" +
            "- create_wall_arc: curved wall in metres; either (startX,startY,endX,endY,midX,midY) or (centerX,centerY,radiusMeters,startAngleDegrees,endAngleDegrees); optional heightMeters, levelName, wallTypeName.\n" +
            "- create_room: centerX, centerY (metres) or center { x, y }; OR \"boundary\" array (>=3 points) to use centroid; optional name, levelName. Walls must enclose the point.\n" +
            "- create_door: locationX, locationY (metres) or location { x, y }; optional hostWallId (integer wall id from context); optional levelName; optional doorTypeName (match namedTypesForRevitOps in context). If hostWallId is omitted, the nearest wall on that level is used.\n" +
            "- create_window: same fields as create_door; optional windowTypeName (match context).\n" +
            "- create_floor_from_room: **PRIMARY for new floors when a Room exists** — roomId (or elementId when the element is a Room). Uses the room's computed boundary (arcs/circles OK). Optional floorTypeName, name (comment), boundaryLocation: center (default), finish, coreBoundary, coreCenter.\n" +
            "- create_floor: **Fallback** when create_floor_from_room is not possible — boundary as array of {x,y} in metres (closed polygon, at least 3 points), OR boundarySegments [{kind:\"line\"|\"arc\",...}], OR circle { center:{x,y} (or centerX/centerY), radiusMeters }. Optional: levelName, floorTypeName (match context), name (comment).\n" +
            "- analyze_floor_wall_footprint: read-only check — floorId or floorIds (from context). **When a Room is found** (optional explicit roomId, or heuristic: room location inside slab outline on same level), **metrics and likelyMismatch use slab vs Room boundary**; otherwise uses wall-chain comparison. Optional roomId, boundaryLocation (same as create_floor_from_room), wallIds for diagnostics / wall_chain fallback, toleranceMeters (default 0.08), areaRatioTolerance (default 0.06). Short log lines; optional includeJson:true.\n" +
            "- repair_floor_to_room_footprint: **preferred repair when analyze used Room as reference** — floorId, roomId; optional boundaryLocation. Recreates the floor like create_floor_from_room; rollback on failure.\n" +
            "- repair_floor_to_wall_footprint: fixes slabs from chained wall location curves when there is no usable Room reference or suggestedRepair says so. Requires floorId; strongly recommended wallIds on busy levels (footprintRepairHints). alignTo: wall_centerline (default), wall_inside, or wall_outside. Model geometry; works in 3D.\n" +
            "- create_ceiling_from_room: **PRIMARY for new ceilings when a Room exists** — roomId (or elementId for Room); optional ceilingTypeName, name, boundaryLocation (same as floor).\n" +
            "- create_ceiling: **Fallback** — same geometry options as create_floor (boundary, boundarySegments, or circle), but ceilingTypeName (match context).\n" +
            "- analyze_ceiling_wall_footprint: same Room-first pattern as floor — ceilingId or ceilingIds; optional roomId, boundaryLocation, wallIds; toleranceMeters, areaRatioTolerance; includeJson optional.\n" +
            "- repair_ceiling_to_room_footprint: ceilingId, roomId; optional boundaryLocation; rollback on failure (like create_ceiling_from_room).\n" +
            "- repair_ceiling_to_wall_footprint: wall-chain repair when no Room reference. ceilingId; optional wallIds; alignTo: wall_centerline (default), wall_inside, or wall_outside. Copies type, offset, comments when possible.\n" +
            "- create_wall_opening: rectangular opening on wall. Host by hostWallId OR locationX/locationY (+ optional levelName). Position by positionAlongWallMeters or positionRatio (0..1) or location projection. Required: openingWidthMeters and openingHeightMeters (or openingTopOffsetMeters with openingBaseOffsetMeters). Optional: maxHostDistanceMeters, autoClamp (true/false).\n" +
            "- create_wall_arch_opening: ONLY when the user explicitly wants a door element (schedules, door type from context, tags as a door) with an arched shape — it hosts a door family on the wall. If they only want a plain arched hole in the wall (no door in door schedules), use create_wall_roman_arch_profile instead. Same host/position fields as create_wall_opening. Optional archTypeName (or doorTypeName), openingWidthMeters, openingHeightMeters, openingBaseOffsetMeters, autoClamp. Choose an ARCHED door type (name should indicate arch/arco/roman/round); avoid plain rectangular door types.\n" +
            "- create_wall_roman_arch_profile: TRUE roman arch void by editing wall profile (no arched door family). hostWallId (straight wall), positionAlongWallMeters or positionRatio, openingWidthMeters, openingBaseOffsetMeters, jambHeightMeters OR openingTotalHeightMeters (total = jamb + width/2), optional autoClamp. Keep openingBaseOffsetMeters at least ~0.05 m or omit — the plugin auto-lifts the void so its boundary never coincides with the wall outer profile (Revit crashes on shared edges). Replaces sketch on that wall; commits prior ops in the batch before this step.\n" +
            "- create_wall_custom_profile_void: CLOSED HOLE through wall thickness (inner profile loop). hostWallId (straight wall). " +
            "One void: root \"shape\" { kind, ... } or \"boundary\" [ { alongMeters, heightFromWallBaseMeters }, ... ] (>=3). " +
            "Several voids: \"voids\": [ { \"shape\": { kind, ... }, \"centerAlongMeters\": ..., \"centerHeightFromWallBaseMeters\": ... } ] — center/rotation may be siblings of \"shape\" in the same object (merged automatically), or inside \"shape\". " +
            "Parametric kinds need centerAlongMeters, centerHeightFromWallBaseMeters (or omit and set positionRatio 0..1 × wall length → centerAlongMeters, heightPositionRatio 0..1 × wall height → center height — plugin fills shape). " +
            "optional rotationDegrees. " +
            "Supported kinds: circle (radiusMeters, optional startAngleDegrees/endAngleDegrees); ellipse (radiusAlongMeters, radiusHeightMeters, optional startAngleDegrees/endAngleDegrees); " +
            "star (outerRadiusMeters, points/tips, optional innerRadiusMeters); regularPolygon (radiusMeters, sides); " +
            "isoscelesTriangle/triangle (baseWidthMeters, heightMeters, optional pointUp); diamond/rhombus (widthAlongMeters, heightMeters); " +
            "cross/plus (horizontalSpanMeters, verticalSpanMeters, armThicknessMeters); heart (scaleMeters — prefer >=0.35 m; plugin tessellates with adaptive chord length). " +
            "For richer geometry, use \"segments\" (either root or inside \"boundary\") with ordered entries of kind line/arc/circle/ellipse and nested points start/end/mid/center in { alongMeters, heightFromWallBaseMeters }; the chain must close (last segment meets first). " +
            "Two arc segments meeting at the sides form a lens (vesica), not an ellipse — for a true ellipse use shape.kind \"ellipse\" or one ellipse segment with full sweep. " +
            "For a round hole use shape.kind \"circle\" (or segments kind circle) with radiusMeters — do NOT approximate circles with few boundary line segments (octagon). " +
            "For an elliptical hole use shape.kind \"ellipse\" with radiusAlongMeters and radiusHeightMeters (not an octagon boundary). " +
            "For slots, rounded rectangles, capsules, squircles, or any other silhouette use \"boundary\" with explicit points or segment chains. " +
            "For roman arch wall openings use create_wall_roman_arch_profile (not this op). " +
            "clampToWallShell default true. Replaces wall profile sketch; commits prior batch ops like create_wall_roman_arch_profile.\n" +
            "- flip_wall: elementIds (array) or elementId — flips wall facing.\n" +
            "- create_family_instance: familyTypeName (required — type name or \"Family : Type\" from context namedTypesForRevitOps.sampleLoadableFamilyTypes); locationX/locationY or location {x,y}; optional levelName; optional rotationDegrees (plan rotation).\n" +
            "- create_level: name (string), elevationMeters (number — metres from internal origin, same convention as sketch XY origin height reference).\n" +
            "- create_grid: startX, startY, endX, endY (metres — axis line in plan); optional levelName (sets work plane elevation); optional gridName or name for the grid label.\n" +
            "- change_element_level: default when the user wants to move elements to another level without keeping world position. Same ids/level fields as below; optional preserveWorldPosition (boolean, default false) or preservePosition — set true only when the user explicitly asks to keep the same XYZ in the model.\n" +
            "- change_level_preserve_position: same fields as change_element_level but always preserves world Z (equivalent to preserveWorldPosition true). Use when the user clearly wants height/position unchanged in space.\n" +
            "  Common fields for both: elementIds (array) and/or elementId; targetLevelName or targetLevelId. Supported: FamilyInstance (level-hosted), Wall, Floor, Ceiling.\n" +
            "All revitOps in the JSON array are applied in one Revit transaction except create_wall_roman_arch_profile and create_wall_custom_profile_void (they commit earlier ops first). Very large batches can be slow or fail mid-run — prefer reasonable sizes or the Sketch-to-BIM flow for full floor plans.\n" +
            "Use only element ids from the Revit context when possible. Prefer few, safe operations; the user can run the chat again.";

        private readonly PluginSettings _settings;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

        public LlmChatService(PluginSettings settings)
        {
            _settings = settings;
        }

        /// <param name="revitContextForSystem">Optional Revit JSON/text merged into the system instruction each call.</param>
        public Task<string> CompleteAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem = null)
        {
            var provider = string.IsNullOrWhiteSpace(_settings.LlmProvider)
                ? "Ollama"
                : _settings.LlmProvider.Trim();

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteOllamaAsync(turns, revitContextForSystem);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteGeminiAsync(turns, revitContextForSystem);
            }

            if (string.Equals(provider, "Nvidia", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteNvidiaOpenAiAsync(turns, revitContextForSystem);
            }

            throw new InvalidOperationException(
                "Unknown LlmProvider in pluginsettings.json: \"" + provider + "\". Use \"Ollama\", \"Gemini\", or \"Nvidia\".");
        }

        private static string MergeSystemPrompt(string? revitContextForSystem)
        {
            if (string.IsNullOrWhiteSpace(revitContextForSystem))
            {
                return SystemPromptBase;
            }

            return SystemPromptBase + "\n\n### Revit context (from plugin)\n" + revitContextForSystem.Trim();
        }

        private async Task<string> CompleteOllamaAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem)
        {
            var baseUrl = NormalizeOllamaBaseUrl(_settings.OllamaBaseUrl);
            var model = string.IsNullOrWhiteSpace(_settings.OllamaModel) ? "llava" : _settings.OllamaModel.Trim();
            var url = baseUrl.TrimEnd('/') + "/api/chat";

            var messages = new List<object>
            {
                new { role = "system", content = MergeSystemPrompt(revitContextForSystem) }
            };

            foreach (var turn in turns)
            {
                var trimmed = turn.Text?.Trim();
                if (string.IsNullOrEmpty(trimmed) && string.IsNullOrEmpty(turn.ImageBase64))
                {
                    continue;
                }

                var text = string.IsNullOrEmpty(trimmed) ? "(image attached)" : trimmed;
                if (turn.IsUser && !string.IsNullOrEmpty(turn.ImageBase64))
                {
                    messages.Add(new
                    {
                        role = "user",
                        content = text,
                        images = new[] { turn.ImageBase64 }
                    });
                }
                else
                {
                    messages.Add(new
                    {
                        role = turn.IsUser ? "user" : "assistant",
                        content = text
                    });
                }
            }

            var body = new { model, stream = false, messages };
            var json = JsonConvert.SerializeObject(body);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                var response = await Http.PostAsync(url, content).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "Ollama recusou o pedido (" + (int)response.StatusCode + ").\n\n" +
                        "Para imagens usa um modelo com visão (ex. llava, qwen2-vl). URL: " + url + "\n\n---\n" + payload);
                }

                return ExtractOllamaAssistantContent(payload);
            }
        }

        private async Task<string> CompleteGeminiAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                throw new InvalidOperationException("GeminiApiKey is empty. Set it in pluginsettings.json.");
            }

            var modelId = NormalizeGeminiModelId(_settings.GeminiModel);
            var url =
                "https://generativelanguage.googleapis.com/v1beta/models/" +
                modelId +
                ":generateContent?key=" +
                Uri.EscapeDataString(_settings.GeminiApiKey);

            var contents = new List<object>();
            foreach (var turn in turns)
            {
                var trimmed = turn.Text?.Trim();
                if (string.IsNullOrEmpty(trimmed) && string.IsNullOrEmpty(turn.ImageBase64))
                {
                    continue;
                }

                var text = string.IsNullOrEmpty(trimmed) ? "(image attached)" : trimmed;
                var role = turn.IsUser ? "user" : "model";
                if (turn.IsUser && !string.IsNullOrEmpty(turn.ImageBase64))
                {
                    var mime = string.IsNullOrWhiteSpace(turn.ImageMimeType) ? "image/png" : turn.ImageMimeType;
                    contents.Add(new
                    {
                        role,
                        parts = new object[]
                        {
                            new { text },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = mime,
                                    data = turn.ImageBase64
                                }
                            }
                        }
                    });
                }
                else
                {
                    contents.Add(new
                    {
                        role,
                        parts = new object[] { new { text } }
                    });
                }
            }

            if (contents.Count == 0)
            {
                throw new InvalidOperationException("No messages to send.");
            }

            var body = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = MergeSystemPrompt(revitContextForSystem) } }
                },
                contents
            };

            var json = JsonConvert.SerializeObject(body);
            var response = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Gemini API error: " + (int)response.StatusCode + "\n" + payload);
            }

            return SketchInterpretationParser.ExtractAssistantTextFromGeminiResponse(payload);
        }

        private async Task<string> CompleteNvidiaOpenAiAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem)
        {
            if (string.IsNullOrWhiteSpace(_settings.NvidiaApiKey))
            {
                throw new InvalidOperationException("NvidiaApiKey is empty. Set it in pluginsettings.json.");
            }

            var url = NormalizeNvidiaChatCompletionsUrl(_settings.NvidiaChatCompletionsUrl);
            var model = string.IsNullOrWhiteSpace(_settings.NvidiaModel)
                ? "google/gemma-3n-e4b-it"
                : _settings.NvidiaModel.Trim();

            var messages = new List<object>
            {
                new { role = "system", content = MergeSystemPrompt(revitContextForSystem) }
            };

            foreach (var turn in turns)
            {
                var trimmed = turn.Text?.Trim();
                if (string.IsNullOrEmpty(trimmed) && string.IsNullOrEmpty(turn.ImageBase64))
                {
                    continue;
                }

                var text = string.IsNullOrEmpty(trimmed) ? "(image attached)" : trimmed;
                if (turn.IsUser && !string.IsNullOrEmpty(turn.ImageBase64))
                {
                    var mime = string.IsNullOrWhiteSpace(turn.ImageMimeType) ? "image/png" : turn.ImageMimeType.Trim();
                    var dataUrl = "data:" + mime + ";base64," + turn.ImageBase64;
                    messages.Add(new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    });
                }
                else
                {
                    messages.Add(new
                    {
                        role = turn.IsUser ? "user" : "assistant",
                        content = text
                    });
                }
            }

            if (messages.Count <= 1)
            {
                throw new InvalidOperationException("No user/assistant messages to send.");
            }

            var body = new
            {
                model,
                messages,
                stream = false,
                max_tokens = 8192,
                temperature = 0.2,
                top_p = 0.7,
                frequency_penalty = 0.0,
                presence_penalty = 0.0
            };

            var json = JsonConvert.SerializeObject(body);
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.NvidiaApiKey.Trim());
                req.Headers.Accept.ParseAdd("application/json");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await Http.SendAsync(req).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "NVIDIA API error (" + (int)response.StatusCode + "). URL: " + url + "\n\n---\n" + payload);
                }

                return OpenAiChatCompletionParser.ExtractAssistantContent(payload);
            }
        }

        private static string NormalizeNvidiaChatCompletionsUrl(string? url)
        {
            var u = string.IsNullOrWhiteSpace(url)
                ? "https://integrate.api.nvidia.com/v1/chat/completions"
                : url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "https://" + u;
            }

            return u;
        }

        private static string ExtractOllamaAssistantContent(string payload)
        {
            var root = JObject.Parse(payload);
            var text = root["message"]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Ollama returned no message.content. Resposta: " + payload);
            }

            return text.Trim();
        }

        private static string NormalizeOllamaBaseUrl(string? url)
        {
            var u = string.IsNullOrWhiteSpace(url) ? "http://localhost:11434" : url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "http://" + u;
            }

            return u;
        }

        private static string NormalizeGeminiModelId(string? model)
        {
            var id = string.IsNullOrWhiteSpace(model) ? "gemini-2.0-flash" : model.Trim();
            if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring("models/".Length);
            }

            return id;
        }
    }
}
