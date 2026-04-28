using RevitSketchPoC.Contracts;
using System;
using System.Text;

namespace RevitSketchPoC.Services
{
    /// <summary>
    /// Shared vision+JSON instructions so Ollama/Gemini produce axis-aligned floor plans at real scale (meters).
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
            sb.AppendLine("You are reading a 2D architectural SKETCH (top view). Follow these rules strictly:");
            sb.AppendLine("- Use METERS. Put origin (0,0) at the bottom-left corner of the OUTER building footprint.");
            sb.AppendLine("- If the drawing shows numeric dimensions (e.g. 6m, 12m, 17m), use those exact lengths for walls. Do not invent random decimals.");
            sb.AppendLine("- Walls must be mostly AXIS-ALIGNED (horizontal or vertical segments). Diagonals only if clearly drawn.");
            sb.AppendLine("- Each wall is one straight segment: start (x,y) to end (x,y) in meters, same z plane.");
            sb.AppendLine("- Draw the FULL outer rectangle of the house first, then interior partition walls.");
            sb.AppendLine("- Rooms: closed polygon boundary in order (each corner once). Name rooms from labels on the sketch (Kitchen, Bedroom, Bathroom, etc.).");
            sb.AppendLine("- Doors: approximate center point on the wall line where a door symbol is drawn.");
            sb.AppendLine("- Do NOT output tiny segments shorter than 0.35 m (noise). Merge collinear segments when possible.");
            sb.AppendLine("- Typical single-storey sketch: footprint might be around 12–20 m wide and 10–15 m deep if dimensions say so.");
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
    }
}
