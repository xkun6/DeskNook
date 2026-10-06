namespace DeskNext.Model;

public readonly record struct GridSize(int Cols, int Rows);

/// <summary>某显示器的图标网格：工作区为物理像素，格子尺寸为 DIP。</summary>
public sealed record MonitorGrid(string Name, double Scale, int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight, double CellW, double CellH)
{
    public int Cols => Math.Max(1, (int)Math.Floor(WorkWidth / Scale / CellW));
    public int Rows => Math.Max(1, (int)Math.Floor(WorkHeight / Scale / CellH));
    public GridSize Size => new(Cols, Rows);
}
