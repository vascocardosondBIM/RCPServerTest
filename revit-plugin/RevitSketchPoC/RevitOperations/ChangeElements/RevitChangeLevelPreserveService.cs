using Autodesk.Revit.DB;
using System;

namespace RevitSketchPoC.RevitOperations.ChangeElements
{
    /// <summary>
    /// Mudança de nível: modo simples (referência ao nível + offset/altura a zero) ou preservação da cota Z no modelo
    /// (lógica alinhada a ChangeElementLevel / LevelPreserveService).
    /// </summary>
    public static class RevitChangeLevelPreserveService
    {
        private static readonly BuiltInParameter[] OffsetCandidates =
        {
            BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
            BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            BuiltInParameter.INSTANCE_ELEVATION_PARAM,
            BuiltInParameter.Z_OFFSET_VALUE
        };

        /// <param name="preserveWorldPosition">Se true, mantém XYZ no mundo; se false, associa ao nível pedido com offset/altura zero (comportamento típico de “mudar para o nível”).</param>
        public static bool TryReassignLevel(
            Document doc,
            Element element,
            Level newLevel,
            bool preserveWorldPosition,
            out string? failReason)
        {
            failReason = null;

            if (element.Pinned)
            {
                failReason = "Elemento está fixo (pinned).";
                return false;
            }

            if (element is FamilyInstance fi)
            {
                return preserveWorldPosition
                    ? TryFamilyInstancePreserve(doc, fi, newLevel, out failReason)
                    : TryFamilyInstanceSimple(fi, newLevel, out failReason);
            }

            if (element is Wall wall)
            {
                return preserveWorldPosition
                    ? TryWallPreserve(doc, wall, newLevel, out failReason)
                    : TryWallSimple(wall, newLevel, out failReason);
            }

            if (element is Floor floor)
            {
                return preserveWorldPosition
                    ? TryFloorPreserve(doc, floor, newLevel, out failReason)
                    : TryFloorSimple(floor, newLevel, out failReason);
            }

            if (element is Ceiling ceiling)
            {
                return preserveWorldPosition
                    ? TryCeilingPreserve(doc, ceiling, newLevel, out failReason)
                    : TryCeilingSimple(ceiling, newLevel, out failReason);
            }

            failReason = "Tipo não suportado: " + element.GetType().Name + " (só FamilyInstance, Wall, Floor, Ceiling).";
            return false;
        }

        private static bool TryFamilyInstancePreserve(Document doc, FamilyInstance fi, Level newLevel, out string? failReason)
        {
            failReason = null;

            if (fi.Host is not null && fi.Host is not Level)
            {
                failReason = "Família alojada num host que não é nível (ex.: parede).";
                return false;
            }

            if (!TryGetReferenceZ(fi, out double refZ))
            {
                failReason = "Não foi possível obter a cota Z de referência.";
                return false;
            }

            ElementId currentLevelId = fi.LevelId;
            if (currentLevelId == ElementId.InvalidElementId)
            {
                failReason = "Instância sem referência de nível.";
                return false;
            }

            if (currentLevelId == newLevel.Id)
            {
                failReason = "Já está neste nível.";
                return false;
            }

            if (doc.GetElement(currentLevelId) is not Level)
            {
                failReason = "Nível atual inválido.";
                return false;
            }

            double newOffset = refZ - newLevel.Elevation;

            if (!TrySetFamilyInstanceLevel(fi, newLevel.Id, out failReason))
            {
                return false;
            }

            if (!TrySetVerticalOffset(fi, newOffset, out failReason))
            {
                TrySetFamilyInstanceLevel(fi, currentLevelId, out _);
                return false;
            }

            return true;
        }

        /// <summary>Associa ao nível pedido e zera offset vertical (família assente no plano do nível).</summary>
        private static bool TryFamilyInstanceSimple(FamilyInstance fi, Level newLevel, out string? failReason)
        {
            failReason = null;

            if (fi.Host is not null && fi.Host is not Level)
            {
                failReason = "Família alojada num host que não é nível (ex.: parede).";
                return false;
            }

            ElementId currentLevelId = fi.LevelId;
            if (currentLevelId == ElementId.InvalidElementId)
            {
                failReason = "Instância sem referência de nível.";
                return false;
            }

            if (currentLevelId == newLevel.Id)
            {
                failReason = "Já está neste nível.";
                return false;
            }

            if (!TrySetFamilyInstanceLevel(fi, newLevel.Id, out failReason))
            {
                return false;
            }

            if (!TrySetVerticalOffset(fi, 0.0, out failReason))
            {
                TrySetFamilyInstanceLevel(fi, currentLevelId, out _);
                return false;
            }

            return true;
        }

        private static bool TryWallPreserve(Document doc, Wall wall, Level newLevel, out string? failReason)
        {
            failReason = null;

            Parameter? baseConstraint = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Parameter? baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);

            if (baseConstraint is null || baseOffset is null || baseConstraint.StorageType != StorageType.ElementId)
            {
                failReason = "Parâmetros de base da parede indisponíveis.";
                return false;
            }

            ElementId baseLevelId = baseConstraint.AsElementId();
            if (baseLevelId == ElementId.InvalidElementId || doc.GetElement(baseLevelId) is not Level oldBase)
            {
                failReason = "Nível de base da parede inválido.";
                return false;
            }

            if (baseLevelId == newLevel.Id)
            {
                failReason = "Parede já referencia este nível de base.";
                return false;
            }

            double baseZ = oldBase.Elevation + baseOffset.AsDouble();
            double newBaseOffset = baseZ - newLevel.Elevation;

            if (baseConstraint.IsReadOnly || baseOffset.IsReadOnly)
            {
                failReason = "Parâmetros de nível/offset da parede são só leitura.";
                return false;
            }

            baseConstraint.Set(newLevel.Id);
            baseOffset.Set(newBaseOffset);
            return true;
        }

        private static bool TryWallSimple(Wall wall, Level newLevel, out string? failReason)
        {
            failReason = null;

            Parameter? baseConstraint = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Parameter? baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);

            if (baseConstraint is null || baseOffset is null || baseConstraint.StorageType != StorageType.ElementId)
            {
                failReason = "Parâmetros de base da parede indisponíveis.";
                return false;
            }

            ElementId baseLevelId = baseConstraint.AsElementId();
            if (baseLevelId == ElementId.InvalidElementId || wall.Document.GetElement(baseLevelId) is not Level)
            {
                failReason = "Nível de base da parede inválido.";
                return false;
            }

            if (baseLevelId == newLevel.Id)
            {
                failReason = "Parede já referencia este nível de base.";
                return false;
            }

            if (baseConstraint.IsReadOnly || baseOffset.IsReadOnly)
            {
                failReason = "Parâmetros de nível/offset da parede são só leitura.";
                return false;
            }

            baseConstraint.Set(newLevel.Id);
            baseOffset.Set(0.0);
            return true;
        }

        private static bool TryFloorPreserve(Document doc, Floor floor, Level newLevel, out string? failReason)
        {
            failReason = null;

            Parameter? levelParam = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            Parameter? heightParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);

            if (levelParam is null || heightParam is null || levelParam.StorageType != StorageType.ElementId)
            {
                failReason = "Parâmetros do piso em falta.";
                return false;
            }

            ElementId levelId = levelParam.AsElementId();
            if (levelId == ElementId.InvalidElementId || doc.GetElement(levelId) is not Level oldLevel)
            {
                failReason = "Nível do piso inválido.";
                return false;
            }

            if (levelId == newLevel.Id)
            {
                failReason = "Piso já neste nível.";
                return false;
            }

            double refZ = oldLevel.Elevation + heightParam.AsDouble();
            double newHeight = refZ - newLevel.Elevation;

            if (levelParam.IsReadOnly || heightParam.IsReadOnly)
            {
                failReason = "Parâmetros do piso são só leitura.";
                return false;
            }

            levelParam.Set(newLevel.Id);
            heightParam.Set(newHeight);
            return true;
        }

        private static bool TryFloorSimple(Floor floor, Level newLevel, out string? failReason)
        {
            failReason = null;

            Parameter? levelParam = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            Parameter? heightParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);

            if (levelParam is null || heightParam is null || levelParam.StorageType != StorageType.ElementId)
            {
                failReason = "Parâmetros do piso em falta.";
                return false;
            }

            ElementId levelId = levelParam.AsElementId();
            if (levelId == ElementId.InvalidElementId || floor.Document.GetElement(levelId) is not Level)
            {
                failReason = "Nível do piso inválido.";
                return false;
            }

            if (levelId == newLevel.Id)
            {
                failReason = "Piso já neste nível.";
                return false;
            }

            if (levelParam.IsReadOnly || heightParam.IsReadOnly)
            {
                failReason = "Parâmetros do piso são só leitura.";
                return false;
            }

            levelParam.Set(newLevel.Id);
            heightParam.Set(0.0);
            return true;
        }

        private static bool TryCeilingPreserve(Document doc, Ceiling ceiling, Level newLevel, out string? failReason)
        {
            failReason = null;

            Parameter? levelParam = ceiling.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            Parameter? heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);

            if (levelParam is null || heightParam is null || levelParam.StorageType != StorageType.ElementId)
            {
                failReason = "Parâmetros do teto em falta.";
                return false;
            }

            ElementId levelId = levelParam.AsElementId();
            if (levelId == ElementId.InvalidElementId || doc.GetElement(levelId) is not Level oldLevel)
            {
                failReason = "Nível do teto inválido.";
                return false;
            }

            if (levelId == newLevel.Id)
            {
                failReason = "Teto já neste nível.";
                return false;
            }

            double refZ = oldLevel.Elevation + heightParam.AsDouble();
            double newHeight = refZ - newLevel.Elevation;

            if (levelParam.IsReadOnly || heightParam.IsReadOnly)
            {
                failReason = "Parâmetros do teto são só leitura.";
                return false;
            }

            levelParam.Set(newLevel.Id);
            heightParam.Set(newHeight);
            return true;
        }

        private static bool TryCeilingSimple(Ceiling ceiling, Level newLevel, out string? failReason)
        {
            failReason = null;

            Parameter? levelParam = ceiling.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            Parameter? heightParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);

            if (levelParam is null || heightParam is null || levelParam.StorageType != StorageType.ElementId)
            {
                failReason = "Parâmetros do teto em falta.";
                return false;
            }

            ElementId levelId = levelParam.AsElementId();
            if (levelId == ElementId.InvalidElementId || ceiling.Document.GetElement(levelId) is not Level)
            {
                failReason = "Nível do teto inválido.";
                return false;
            }

            if (levelId == newLevel.Id)
            {
                failReason = "Teto já neste nível.";
                return false;
            }

            if (levelParam.IsReadOnly || heightParam.IsReadOnly)
            {
                failReason = "Parâmetros do teto são só leitura.";
                return false;
            }

            levelParam.Set(newLevel.Id);
            heightParam.Set(0.0);
            return true;
        }

        private static bool TryGetReferenceZ(FamilyInstance fi, out double z)
        {
            z = 0;
            switch (fi.Location)
            {
                case LocationPoint lp:
                    z = lp.Point.Z;
                    return true;
                case LocationCurve lc:
                    XYZ p0 = lc.Curve.GetEndPoint(0);
                    XYZ p1 = lc.Curve.GetEndPoint(1);
                    z = 0.5 * (p0.Z + p1.Z);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TrySetFamilyInstanceLevel(FamilyInstance fi, ElementId newLevelId, out string? failReason)
        {
            failReason = null;

            Parameter? p = fi.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                           ?? fi.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);

            if (p is not null && p.StorageType == StorageType.ElementId && !p.IsReadOnly)
            {
                try
                {
                    p.Set(newLevelId);
                    return true;
                }
                catch (Exception ex)
                {
                    failReason = "Erro ao definir nível: " + ex.Message;
                    return false;
                }
            }

            failReason = "Parâmetro de nível da família não editável.";
            return false;
        }

        private static bool TrySetVerticalOffset(FamilyInstance fi, double offsetFeet, out string? failReason)
        {
            failReason = null;

            foreach (BuiltInParameter bip in OffsetCandidates)
            {
                Parameter? p = fi.get_Parameter(bip);
                if (p is null || p.IsReadOnly || p.StorageType != StorageType.Double)
                {
                    continue;
                }

                try
                {
                    p.Set(offsetFeet);
                    return true;
                }
                catch
                {
                    // Tentar o próximo candidato.
                }
            }

            failReason = "Não foi encontrado parâmetro de offset vertical editável.";
            return false;
        }
    }
}
