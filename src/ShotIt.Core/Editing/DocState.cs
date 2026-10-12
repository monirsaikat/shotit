using System.Collections.Immutable;
using ShotIt.Core.Annotations;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Editing;

public enum BackgroundKind
{
    None,
    Solid,
    Gradient,
}

/// <summary>Presentation frame around the screenshot: padding, backdrop, rounded corners, shadow.</summary>
public sealed record CanvasStyle
{
    public static readonly CanvasStyle None = new();

    public double Padding { get; init; }
    public BackgroundKind Background { get; init; } = BackgroundKind.None;
    public ColorRgba Color1 { get; init; } = new(0x4F, 0x6B, 0xFF);
    public ColorRgba Color2 { get; init; } = new(0xB1, 0x4F, 0xFF);
    public double CornerRadius { get; init; }
    public bool Shadow { get; init; }

    public bool IsNone => Padding <= 0 && Background == BackgroundKind.None && CornerRadius <= 0 && !Shadow;
}

public enum WatermarkPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
}

public sealed record Watermark
{
    public string Text { get; init; } = string.Empty;
    public WatermarkPosition Position { get; init; } = WatermarkPosition.BottomRight;
    public double Opacity { get; init; } = 0.5;
    public double FontSize { get; init; } = 18;
    public ColorRgba Color { get; init; } = ColorRgba.White;
}

/// <summary>
/// The complete, immutable state of a document being edited. Undo history is simply a stack of these;
/// unchanged parts (the base image, untouched annotations) are shared between snapshots.
/// </summary>
public sealed record DocState(PixelBuffer Base, ImmutableList<Annotation> Items)
{
    public CanvasStyle Canvas { get; init; } = CanvasStyle.None;
    public Watermark? Watermark { get; init; }

    public PixelSize Size => Base.Size;

    public static DocState FromImage(PixelBuffer image) => new(image, []);

    public Annotation? Find(Guid id) => Items.FirstOrDefault(a => a.Id == id);

    public DocState Replace(Annotation updated)
    {
        int index = Items.FindIndex(a => a.Id == updated.Id);
        return index < 0 ? this : this with { Items = Items.SetItem(index, updated) };
    }

    public DocState Add(Annotation item) => this with { Items = Items.Add(item) };

    public DocState Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return this with { Items = Items.RemoveAll(a => set.Contains(a.Id)) };
    }

    /// <summary>The next number for a step badge: one more than the highest already present.</summary>
    public int NextStepNumber() =>
        Items.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>
    /// Crops the base image and moves annotations so they stay over the same pixels.
    /// Returns the same state when the rectangle does not intersect the image.
    /// </summary>
    public DocState Crop(PixelRect rect)
    {
        var r = rect.Intersect(Base.Bounds);
        if (r.IsEmpty || r == Base.Bounds) return this;
        var shift = Affine2.Translation(new Vec2(-r.X, -r.Y));
        return this with
        {
            Base = Base.Crop(r),
            Items = [.. Items.Select(a => a.Transform(shift))],
        };
    }
}
