using System.Diagnostics;

namespace Proton.Sdk.Drive;

public static class BlockUtils
{
    public static (int BlockNumber, int BlockIndex) GetBlockIndexFromFileIndex(IReadOnlyList<int> blockSizes, long fileIndex)
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

        return (currentBlock, (int)(fileIndex - currentFileIndex));
    }

    public static void SelfTest()
    {
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 0) == (0, 0));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 5) == (0, 5));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 9) == (0, 9));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 10) == (1, 0));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 11) == (1, 1));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 19) == (1, 9));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 20) == (2, 0));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 21) == (2, 1));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 10], 999) == (2, 979));
        Trace.Assert(GetBlockIndexFromFileIndex([10, 5, 5], 17) == (2, 2));
    }
}
