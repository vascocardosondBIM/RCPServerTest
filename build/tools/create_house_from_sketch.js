import { z } from "zod";
import { withRevitConnectionOptions } from "../utils/ConnectionManager.js";

const sketchInputSchema = z
    .object({
    imagePath: z
        .string()
        .optional()
        .describe("Absolute path to the sketch image on the plugin machine."),
    imageBase64: z
        .string()
        .optional()
        .describe("Base64 image payload. Use this if you cannot pass a local file path."),
    mimeType: z
        .string()
        .optional()
        .default("image/png")
        .describe("Image MIME type when imageBase64 is used, e.g. image/png."),
    prompt: z
        .string()
        .optional()
        .describe("Optional additional prompt/instructions for Gemini interpretation."),
    targetLevelName: z
        .string()
        .optional()
        .describe("Target Revit level name where model elements should be created."),
    wallTypeName: z
        .string()
        .optional()
        .describe("Preferred Revit wall type name to use when creating walls."),
    autoCreateRooms: z
        .boolean()
        .optional()
        .default(true)
        .describe("Create Revit rooms from interpreted room polygons."),
    autoCreateDoors: z
        .boolean()
        .optional()
        .default(true)
        .describe("Create Revit doors from interpreted door openings."),
    showPreviewUi: z
        .boolean()
        .optional()
        .default(true)
        .describe("If true, plugin should show a WPF preview/confirmation UI before commit."),
    pluginPort: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Optional TCP port for the isolated Revit plugin. Defaults to REVIT_SKETCH_PORT or 8081."),
})
    .superRefine((args, ctx) => {
    if (!args.imagePath && !args.imageBase64) {
        ctx.addIssue({
            code: z.ZodIssueCode.custom,
            message: "Provide either imagePath or imageBase64.",
            path: ["imagePath"],
        });
    }
});

export function registerCreateHouseFromSketchTool(server) {
    server.tool("create_house_from_sketch", "Interpret a floor-plan sketch image with Gemini and create a house model in Revit. The heavy lifting happens in the C# plugin (WPF + Revit API). This MCP tool only bridges the request.", {
        data: sketchInputSchema.describe("Sketch interpretation and model-creation settings."),
    }, async (args) => {
        try {
            const pluginPort = args.data.pluginPort || Number(process.env.REVIT_SKETCH_PORT || 8081);
            const { pluginPort: _ignored, ...revitPayload } = args.data;
            const response = await withRevitConnectionOptions({ port: pluginPort }, async (revitClient) => {
                return await revitClient.sendCommand("create_house_from_sketch", revitPayload);
            });
            return {
                content: [
                    {
                        type: "text",
                        text: JSON.stringify(response, null, 2),
                    },
                ],
            };
        }
        catch (error) {
            return {
                content: [
                    {
                        type: "text",
                        text: `create_house_from_sketch failed: ${error instanceof Error ? error.message : String(error)}`,
                    },
                ],
            };
        }
    });
}
