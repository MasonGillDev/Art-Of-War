namespace EmpireGame.Factories
{
    public static class StructureFactory
    {
        public static bool IsValidStructure(StructureType type, Tile tile)
        {
            if (tile.Structure != null)
            {
                return false;
            }

            switch (type)
            {
                case StructureType.Farm:
                    if (tile.TerrainType != Terrains.Grass)
                    {
                        return false;

                    }
                    break;
                case StructureType.Barracs:
                case StructureType.House:
                case StructureType.Blacksmith:
                case StructureType.Wall:
                    if (tile.TerrainType == Terrains.River || tile.TerrainType == Terrains.Cliff || tile.TerrainType == Terrains.Lake)
                    {
                        return false;
                    }
                    break;
                case StructureType.Mine:
                    if (tile.TerrainType != Terrains.Cliff)
                    {
                        return false;
                    }
                    break;
                default:
                    break;
            }
            return true;

        }


        public static Structure? CreateStructure(Tile tile, StructureType structure, Map map)
        {
            if (IsValidStructure(structure, tile))
            {
                //Map the Type to the Structure Type object and create it and add it to the tile.
                switch (structure)
                {
                    case StructureType.Farm:
                        Farm farm = new Farm(map, tile);
                        tile.Structure = farm;
                        return farm;
                    default:
                        break;
                }
            }
            return null;
        }

        public static StructureType[]? ValidStructures(Tile tile)
        {
            Terrains tileTerrain = tile.TerrainType;


            switch (tileTerrain)
            {
                case Terrains.Grass:
                    return new StructureType[] { StructureType.Farm, StructureType.Wall, StructureType.Blacksmith, StructureType.House, StructureType.Barracs };
                case Terrains.Cliff:
                    return new StructureType[] { StructureType.Mine };
                default:
                    return null;
            }
        }

        public static void DestroyStructure(Tile tile){

        }
    }
}