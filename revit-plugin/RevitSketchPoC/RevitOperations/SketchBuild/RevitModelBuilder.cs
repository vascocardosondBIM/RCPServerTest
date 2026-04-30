using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.RevitOperations.CreateElements;
using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Sketch.Services;
using System;

namespace RevitSketchPoC.RevitOperations.SketchBuild
{
    /// <summary>Sketch pipeline entry: sanitizes interpretation then delegates walls / rooms / doors to shared operations.</summary>
    public sealed class RevitModelBuilder
    {
        private readonly PluginSettings _settings;

        public RevitModelBuilder(PluginSettings settings)
        {
            _settings = settings;
        }

        public BuildResult Build(UIDocument uiDoc, SketchToBimRequest request, SketchInterpretation interpretation)
        {
            SketchInterpretationSanitizer.SanitizeInPlace(interpretation);
            if (interpretation.Walls.Count == 0)
            {
                throw new InvalidOperationException(
                    "Depois de filtrar paredes demasiado curtas, não sobrou nenhuma parede válida. " +
                    "O modelo provavelmente devolveu coordenadas erradas ou fora de escala; tenta outro modelo Ollama (ex. maior) ou refina o prompt.");
            }

            var doc = uiDoc.Document;
            var result = new BuildResult { Ok = true, Notes = interpretation.Notes };
            var defaultH = _settings.DefaultWallHeightMeters > 0 ? _settings.DefaultWallHeightMeters : 3.0;

            using (var tx = new Transaction(doc, "Sketch to BIM"))
            {
                tx.Start();

                var level = RevitWallCreationOps.ResolveLevel(doc, request.TargetLevelName);
                var wallType = RevitWallCreationOps.ResolveWallType(doc, request.WallTypeName);
                var createdWalls = RevitWallCreationOps.CreateWallsFromSegments(
                    doc, level, wallType, interpretation.Walls, defaultH);
                result.WallsCreated = createdWalls.Count;

                if (request.AutoCreateRooms)
                {
                    result.RoomsCreated = RevitRoomCreationOps.CreateRoomsFromRegions(doc, level, interpretation.Rooms);
                }

                if (request.AutoCreateDoors)
                {
                    result.DoorsCreated = RevitDoorCreationOps.CreateDoorsFromPlacements(
                        doc, level, createdWalls, interpretation.Doors);
                }

                tx.Commit();
            }

            return result;
        }
    }
}
