using System;
using System.Collections.Generic;
using System.Numerics;

namespace WindUpKey.Quest;

/// <summary>
/// The 4×4 sticker board as a 16-bit mask (bit <c>row * 4 + column</c>). Ten lines: four rows, four columns and the two
/// diagonals. A finished line's cells are wiped and the rest of the board stays, so it never fills (the last empty cell
/// always finishes its row).
/// </summary>
public static class StickerBoard
{
    public const int Size = 4;

    public const int CellCount = Size * Size;

    /// <summary>The ten line masks: rows, then columns, then the two diagonals.</summary>
    public static readonly IReadOnlyList<ushort> Lines = BuildLines();

    public static bool HasSticker(ushort board, int cell) => (board & (1 << cell)) != 0;

    public static int EmptyCount(ushort board) => CellCount - BitOperations.PopCount(board);

    /// <summary>
    /// Puts a sticker in the <paramref name="emptyIndex"/>-th empty cell (row-major, 0-based) and returns the new board.
    /// </summary>
    public static ushort Place(ushort board, int emptyIndex, out int cell)
    {
        if (emptyIndex < 0 || emptyIndex >= EmptyCount(board))
            throw new ArgumentOutOfRangeException(nameof(emptyIndex));

        for (cell = 0; cell < CellCount; cell++)
        {
            if (HasSticker(board, cell))
                continue;
            if (emptyIndex-- == 0)
                return (ushort)(board | (1 << cell));
        }

        throw new InvalidOperationException("No empty cell.");
    }

    /// <summary>How many lines are complete on <paramref name="board"/>.</summary>
    public static int CompletedLines(ushort board)
    {
        var count = 0;
        foreach (var line in Lines)
        {
            if ((board & line) == line)
                count++;
        }

        return count;
    }

    /// <summary>The cells of every complete line on <paramref name="board"/>.</summary>
    public static ushort CompletedLineCells(ushort board)
    {
        var cells = 0;
        foreach (var line in Lines)
        {
            if ((board & line) == line)
                cells |= line;
        }

        return (ushort)cells;
    }

    private static ushort[] BuildLines()
    {
        var lines = new List<ushort>();
        for (var row = 0; row < Size; row++)
        {
            var mask = 0;
            for (var col = 0; col < Size; col++)
                mask |= 1 << (row * Size + col);
            lines.Add((ushort)mask);
        }

        for (var col = 0; col < Size; col++)
        {
            var mask = 0;
            for (var row = 0; row < Size; row++)
                mask |= 1 << (row * Size + col);
            lines.Add((ushort)mask);
        }

        int diagonal = 0, anti = 0;
        for (var i = 0; i < Size; i++)
        {
            diagonal |= 1 << (i * Size + i);
            anti |= 1 << (i * Size + (Size - 1 - i));
        }

        lines.Add((ushort)diagonal);
        lines.Add((ushort)anti);
        return lines.ToArray();
    }
}
