using System.Diagnostics;

namespace Proton.Sdk.Drive;

public static class BlockUtils
{
    public static BlockIndex GetBlockIndexFromFileIndex(IReadOnlyList<int> blockSizes, long fileIndex)
    {
        long currentFileIndex = 0;
        int currentBlock = 0;
        while (currentBlock < blockSizes.Count)
        {
            long nextFileIndex = currentFileIndex + blockSizes[currentBlock];
            if (nextFileIndex > fileIndex)
            {
                break;
            }

            currentFileIndex = nextFileIndex;
            currentBlock++;
        }

        return new BlockIndex(currentBlock, (int)(fileIndex - currentFileIndex));
    }

    public static void SelfTest()
    {
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 0) == new BlockIndex(0, 0));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 5) == new BlockIndex(0, 5));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 9) == new BlockIndex(0, 9));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 10) == new BlockIndex(1, 0));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 11) == new BlockIndex(1, 1));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 19) == new BlockIndex(1, 9));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 20) == new BlockIndex(2, 0));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 21) == new BlockIndex(2, 1));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 999) == new BlockIndex(2, 979));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 5, 5], 17) == new BlockIndex(2, 2));
    }
}

public readonly record struct BlockIndex(int BlockNumber, int IndexWithinBlock);
