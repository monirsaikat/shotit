using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ShotIt.App.Themes;

/// <summary>Toolbar icons as 16x16 stroke paths, so they stay crisp at every DPI and take the button's colour.</summary>
public static class Icons
{
    public const string Select = "M3.5,2 L3.5,13 L6.4,10.4 L8.3,14.6 L10,13.8 L8.1,9.7 L12,9.7 Z";
    public const string Arrow = "M3,13 L13,3 M7,3 L13,3 L13,9";
    public const string Line = "M3,13 L13,3";
    public const string Rectangle = "M2.5,4 H13.5 V12 H2.5 Z";
    public const string RoundedRectangle = "M5,4 H11 A2.5,2.5 0 0 1 13.5,6.5 V9.5 A2.5,2.5 0 0 1 11,12 H5 A2.5,2.5 0 0 1 2.5,9.5 V6.5 A2.5,2.5 0 0 1 5,4 Z";
    public const string Eraser = "M3,10.5 L8.5,3.5 L13,7 L8.5,13 H5.5 Z M5.5,13 H13.5";
    public const string Ellipse = "M2,8 A6,4.5 0 1 0 14,8 A6,4.5 0 1 0 2,8 Z";
    public const string Pen = "M2.5,13.5 L3.5,10 L10.5,3 L13,5.5 L6,12.5 Z M9,4.5 L11.5,7";
    public const string Highlighter = "M2.5,14 H9 M5,11.5 L4,9 L10,2.5 L13.5,6 L7.5,12.5 Z M4.7,10.8 L3,13";
    public const string AreaHighlight = "M2,4 H14 V12 H2 Z M4.5,7 H11.5 M4.5,9.5 H9.5";
    public const string Spotlight = "M2,3 H14 V13 H2 Z M5.2,8 A2.8,2.8 0 1 0 10.8,8 A2.8,2.8 0 1 0 5.2,8 Z";
    public const string Text = "M3,5 V3 H13 V5 M8,3 V13 M6,13 H10";
    public const string Callout = "M2,3 H14 V10 H8 L5,13.5 V10 H2 Z";
    public const string Steps = "M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z M6.8,6.3 L8.4,5 V11";
    public const string Magnifier = "M10.2,10.2 L14,14 M2,6.5 A4.5,4.5 0 1 0 11,6.5 A4.5,4.5 0 1 0 2,6.5 Z";
    public const string Blur = "M8,2 C8,2 3.5,7 3.5,10 A4.5,4.5 0 0 0 12.5,10 C12.5,7 8,2 8,2 Z";
    public const string Pixelate = "M2.5,2.5 H13.5 V13.5 H2.5 Z M6.2,2.5 V13.5 M9.8,2.5 V13.5 M2.5,6.2 H13.5 M2.5,9.8 H13.5";
    public const string Redact = "M2,6 H14 V10 H2 Z M4,8 H12";
    public const string Sticker = "M8,2 L9.8,6 L14,6.4 L10.8,9.2 L11.8,13.5 L8,11.2 L4.2,13.5 L5.2,9.2 L2,6.4 L6.2,6 Z";
    public const string Measure = "M2,11 L11,2 L14,5 L5,14 Z M5,8 L6.5,9.5 M7,6 L8.5,7.5 M9,4 L10.5,5.5";
    public const string Eyedropper = "M3,13 L3,11 L8.5,5.5 L10.5,7.5 L5,13 Z M7.5,4.5 L11.5,8.5 M10,6 L12.3,3.7 M12.3,3.7 A1.4,1.4 0 1 0 12.31,3.7";
    public const string Crop = "M4,1.5 V12 H14.5 M1.5,4 H12 V14.5";
    public const string Undo = "M6,3 L2.5,6.5 L6,10 M2.5,6.5 H10 A3.5,3.5 0 0 1 10,13.5 H7";
    public const string Redo = "M10,3 L13.5,6.5 L10,10 M13.5,6.5 H6 A3.5,3.5 0 0 0 6,13.5 H9";
    public const string Copy = "M5.5,5.5 H13.5 V14 H5.5 Z M3,10.5 V2 H10.5";
    public const string Save = "M2.5,2.5 H11 L13.5,5 V13.5 H2.5 Z M5,2.5 V6 H10 V2.5 M5,13.5 V9.5 H11 V13.5";
    public const string SaveAs = "M7,13.5 H2.5 V2.5 H10 L12.5,5 V7.5 M5,2.5 V6 H9.5 V2.5 M9,14.5 L9.4,12.2 L13.3,8.3 L15,10 L11.2,14 Z";
    public const string Upload = "M8,11 V3 M4.8,6 L8,2.8 L11.2,6 M2.5,10.5 V13.5 H13.5 V10.5";
    public const string Pin = "M9.5,2 L14,6.5 L12.4,7 L10,9.4 L10,12.5 L3.5,6 L6.6,6 L9,3.6 Z M6.6,9.4 L2.5,13.5";
    public const string Ocr = "M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11 M5.2,6.5 V5.5 H10.8 V6.5 M8,5.5 V10.8 M6.8,10.8 H9.2";
    public const string Edit = "M2,14 L2.6,11 L11,2.6 L13.4,5 L5,13.4 Z M9.5,4.1 L11.9,6.5";
    public const string Qr = "M2.5,2.5 H6.5 V6.5 H2.5 Z M9.5,2.5 H13.5 V6.5 H9.5 Z M2.5,9.5 H6.5 V13.5 H2.5 Z M9.5,9.5 H11.2 V11.2 H9.5 Z M12,12 H13.5 V13.5 H12 Z M12.5,9.5 H13.5 M9.5,12.8 V13.5";
    public const string Search = Magnifier;
    public const string Share = "M12,2.2 A1.8,1.8 0 1 0 12.01,2.2 M4,6.2 A1.8,1.8 0 1 0 4.01,6.2 M12,10.2 A1.8,1.8 0 1 0 12.01,10.2 M5.6,7.2 L10.4,4.8 M5.6,8.8 L10.4,11.2";
    public const string Print = "M4.5,6 V2.5 H11.5 V6 M4.5,11 H2.5 V6 H13.5 V11 H11.5 M4.5,9 H11.5 V13.5 H4.5 Z";
    public const string Close = "M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5";
    public const string More = "M3.5,8 h0.01 M8,8 h0.01 M12.5,8 h0.01";
    public const string Fill = "M2.5,2.5 H13.5 V13.5 H2.5 Z M2.5,8 H13.5 M2.5,10.5 H13.5 M2.5,13 H13.5 M2.5,5.5 H13.5";
    public const string Check = "M3,8.5 L6.5,12 L13,4.5";
    public const string Rotate = "M13,8 A5,5 0 1 1 10.5,3.7 M10.5,1.5 V4 H13";
    public const string FlipH = "M8,2 V14 M6,4 L2,8 L6,12 Z M10,4 L14,8 L10,12 Z";
    public const string Frame = "M2,2 H14 V14 H2 Z M4.5,4.5 H11.5 V11.5 H4.5 Z";
    public const string ZoomIn = "M10.2,10.2 L14,14 M2,6.5 A4.5,4.5 0 1 0 11,6.5 A4.5,4.5 0 1 0 2,6.5 Z M4.5,6.5 H8.5 M6.5,4.5 V8.5";
    public const string ZoomOut = "M10.2,10.2 L14,14 M2,6.5 A4.5,4.5 0 1 0 11,6.5 A4.5,4.5 0 1 0 2,6.5 Z M4.5,6.5 H8.5";
    public const string Open = "M2,12.5 V3.5 H6 L7.5,5 H13 V7 M2,12.5 L4,7 H14.5 L12.5,12.5 Z";
    public const string Trash = "M3,4.5 H13 M6,4.5 V2.5 H10 V4.5 M4.2,4.5 L5,13.5 H11 L11.8,4.5 M6.8,7 V11 M9.2,7 V11";
    public const string Dashed = "M2,8 H4.5 M6.8,8 H9.3 M11.5,8 H14";
    public const string DoubleArrow = "M3,8 H13 M6,5 L3,8 L6,11 M10,5 L13,8 L10,11";
    public const string Bold = "M4.5,2.5 H8.5 A2.6,2.6 0 0 1 8.5,7.7 H4.5 Z M4.5,7.7 H9.2 A2.9,2.9 0 0 1 9.2,13.5 H4.5 Z";

    public static Path Create(string data, bool thickDots = false)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Style = (Style)Application.Current.FindResource("BarIcon"),
        };
        if (thickDots) path.StrokeThickness = 2.4;
        return path;
    }
}
