using RevitSketchPoC.Sketch.Contracts;
using System.Text;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Shared vision+JSON instructions so Ollama/Gemini trace the image faithfully (meters, axis-aligned where possible).
    /// </summary>
    public static class SketchLlmPrompts
    {
        public static string BuildForSketchRequest(SketchToBimRequest request)
        {
            var user = string.IsNullOrWhiteSpace(request.Prompt)
                ? "Convert this floor plan sketch into walls, room regions and door points for Revit."
                : request.Prompt.Trim();

            var sb = new StringBuilder();
            sb.AppendLine(user);
            sb.AppendLine();
            AppendImageFidelityRules(sb);
            sb.AppendLine();
            sb.AppendLine("OUTPUT: Return ONLY one JSON object, no markdown, no code fences, no commentary before or after.");
            sb.AppendLine("Schema:");
            sb.AppendLine("{");
            sb.AppendLine("  \"walls\": [{\"start\":{\"x\":0,\"y\":0},\"end\":{\"x\":6,\"y\":0},\"heightMeters\":3.0}],");
            sb.AppendLine("  \"rooms\": [{\"name\":\"Kitchen\",\"boundary\":[{\"x\":0,\"y\":0},{\"x\":6,\"y\":0},{\"x\":6,\"y\":6},{\"x\":0,\"y\":6}]}],");
            sb.AppendLine("  \"doors\": [{\"location\":{\"x\":3,\"y\":0}}],");
            sb.AppendLine("  \"notes\": \"short optional note\"");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendImageFidelityRules(StringBuilder sb)
        {
            sb.AppendLine("You are digitizing a 2D floor plan or sketch shown in the IMAGE. Your job is to REPRODUCE what is drawn, not to redesign a \"nicer\" layout.");
            sb.AppendLine();
            sb.AppendLine("### Fidelity (most important)");
            sb.AppendLine("- Trace ONLY walls that appear as clear lines in the image. Do NOT add partition walls, corridors, or rooms that are not visible.");
            sb.AppendLine("- Preserve the TOPOLOGY: same number of main cells/zones as in the drawing; same adjacencies (which walls meet which).");
            sb.AppendLine("- Preserve PROPORTIONS: measure visually the ratio of widths/heights between segments in the image and keep those ratios in meters (e.g. if the outer box is twice as wide as deep in pixels, keep ~2:1 in meters unless numbers on the drawing say otherwise).");
            sb.AppendLine("- Treat drawn lines as WALL CENTERLINES (single line = one wall axis), not double-line thickness unless the drawing clearly shows two parallel lines for one thick wall.");
            sb.AppendLine("- T-junctions and L-corners in the image must appear as the same junctions in your coordinates (meet at shared endpoints within ~0.05 m).");
            sb.AppendLine();
            sb.AppendLine("### Scale (meters)");
            sb.AppendLine("- Use METERS. Origin (0,0) = bottom-left corner of the OUTER building footprint (smallest axis-aligned rectangle that contains the whole built outline in the image).");
            sb.AppendLine("- If the image shows numeric dimensions (e.g. 6m, 3.00, 6x8), use those EXACT lengths — they override visual proportions for labeled segments.");
            sb.AppendLine("- If there are NO dimensions: pick ONE plausible footprint size consistent with the USER text above (e.g. \"6x8\" => 6 m x 8 m outer box) and scale ALL coordinates so the outer outline matches that size; state the assumption in \"notes\".");
            sb.AppendLine("- Round coordinates to 2 decimal places unless a dimension string gives more precision.");
            sb.AppendLine();
            sb.AppendLine("### Wall segments");
            sb.AppendLine("- Walls are axis-aligned (horizontal or vertical) unless the image clearly shows a diagonal wall.");
            sb.AppendLine("- Each wall is one segment: start (x,y) to end (x,y) in meters, same z plane.");
            sb.AppendLine("- ORDER: (1) List segments that form the OUTER CLOSED boundary first (clockwise or counter-clockwise). (2) Then list every INTERIOR partition in any order, but endpoints must match neighbors.");
            sb.AppendLine("- Merge collinear segments on the same line into one segment when they are clearly one wall in the drawing.");
            sb.AppendLine("- Do NOT output segments shorter than 0.35 m (noise).");
            sb.AppendLine();
            sb.AppendLine("### Rooms");
            sb.AppendLine("- For each closed room zone in the drawing, output one \"rooms\" entry: closed polygon boundary in order (each corner once), vertices on wall intersections.");
            sb.AppendLine("- Room \"name\" must match any readable label in the image; if unreadable, use Room_1, Room_2, ... in a consistent order (e.g. left-to-right, bottom-to-top).");
            sb.AppendLine("- Room boundaries must coincide with your wall layout (same corners as the walls you output).");
            sb.AppendLine();
            sb.AppendLine("### Doors");
            sb.AppendLine("- Output a door ONLY where a door swing symbol or clear door gap appears in the image on a wall.");
            sb.AppendLine("- Place \"location\" at the approximate midpoint of the door opening on that wall segment (in meters).");
            sb.AppendLine("- Do NOT add extra doors for \"circulation\" if the drawing does not show them.");
            sb.AppendLine();
            sb.AppendLine("### Quality check before you answer");
            sb.AppendLine("- Count major enclosed zones in the image vs. your \"rooms\" count — they should match.");
            sb.AppendLine("- Mentally walk from the main entrance (if visible) through door openings: if the drawing shows connectivity, your walls+doors must not block paths that are open in the image.");
        }
    }
}
