using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fufucode.App.Features.Skins;

/// <summary>Original 24-unit outline glyphs. Metro uses square joins; Material/Aero/Luna use rounded joins.
/// Widening produces filled geometries usable by PathIcon, including open strokes.</summary>
internal static class SkinIconSet
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["ThumbLike"]="M7,10 V21 H4 V10 Z M7,10 L11,3 Q14,3 14,7 V9 H20 Q22,9 21,11 L19,19 Q18.5,21 16.5,21 H7",
        ["ThumbDislike"]="M7,14 V3 H4 V14 Z M7,14 L11,21 Q14,21 14,17 V15 H20 Q22,15 21,13 L19,5 Q18.5,3 16.5,3 H7",
        ["Speaker"]="M3,9 H7 L12,4 V20 L7,15 H3 Z M16,8 Q20,12 16,16 M18,5 Q25,12 18,19",
        ["Play"]="M7,4 L20,12 L7,20 Z", ["Pause"]="M8,4 V20 M16,4 V20",
        ["Add"]="M12,4 V20 M4,12 H20", ["Dismiss"]="M5,5 L19,19 M19,5 L5,19",
        ["ArrowEnterLeft"]="M20,5 V13 H4 M9,8 L4,13 L9,18",
        ["ArrowClockwise"]="M20,10 A8,8 0 1 0 19,17 M20,4 V10 H14",
        ["ArrowDown"]="M12,3 V21 M5,14 L12,21 L19,14",
        ["Chat"]="M3,4 H21 V17 H9 L3,21 Z M7,9 H17 M7,13 H14",
        ["CheckmarkCircle"]="M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M7,12 L10,15 L17,8",
        ["ChevronRight"]="M8,4 L16,12 L8,20", ["ChevronDown"]="M4,8 L12,16 L20,8",
        ["ChevronUp"]="M4,16 L12,8 L20,16", ["More"]="M4,12 H5 M11.5,12 H12.5 M19,12 H20",
        ["Copy"]="M8,8 H21 V21 H8 Z M16,4 H3 V16",
        ["DismissCircle"]="M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M8,8 L16,16 M16,8 L8,16",
        ["Document"]="M5,3 H14 L19,8 V21 H5 Z M14,3 V8 H19 M8,12 H16 M8,16 H16",
        ["Edit"]="M4,16 L16,4 L20,8 L8,20 H4 Z M13,7 L17,11",
        ["ErrorCircle"]="M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 V13 M12,17 V18",
        ["Folder"]="M3,5 H10 L13,8 H21 V20 H3 Z",
        ["History"]="M3,10 A9,9 0 1 1 4,17 M3,4 V10 H9 M12,7 V12 L16,15",
        ["Info"]="M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 V7 M10,11 H12 V18 H14",
        ["Lightbulb"]="M8,16 C0,6 7,2 12,2 C17,2 24,6 16,16 V19 H8 Z M9,22 H15",
        ["Person"]="M16,7 A4,4 0 1 1 8,7 A4,4 0 1 1 16,7 M3,21 C3,12 21,12 21,21",
        ["PlugConnected"]="M7,3 V8 M17,3 V8 M5,8 H19 V12 C19,21 5,21 5,12 Z M12,19 V23",
        ["PlugDisconnected"]="M7,3 V8 M17,3 V8 M5,8 H19 V12 C19,21 5,21 5,12 Z M3,3 L21,21",
        ["AppsList"]="M3,3 H9 V9 H3 Z M15,3 H21 V9 H15 Z M3,15 H9 V21 H3 Z M15,15 H21 V21 H15 Z",
        ["PuzzlePiece"]="M4,4 H9 C6,-1 18,-1 15,4 H20 V9 C25,6 25,18 20,15 V20 H15 C18,25 6,25 9,20 H4 V15 C-1,18 -1,6 4,9 Z",
        ["Send"]="M3,3 L22,12 L3,21 L7,12 Z M7,12 H22",
        ["Settings"]="M9,3 H15 L16,7 L20,8 L22,12 L19,15 L18,20 L14,21 L11,18 L6,19 L3,15 L6,12 L4,7 L8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
        ["ShieldCheckmark"]="M12,2 L21,6 V12 Q21,18 12,22 Q3,18 3,12 V6 Z M7,12 L10,15 L17,8",
        ["ShieldError"]="M12,2 L21,6 V12 Q21,18 12,22 Q3,18 3,12 V6 Z M12,7 V13 M12,16 V17",
        ["Sparkle"]="M12,2 L15,9 L22,12 L15,15 L12,22 L9,15 L2,12 L9,9 Z",
        ["RecordStop"]="M5,5 H19 V19 H5 Z",
        ["Warning"]="M12,2 L23,21 H1 Z M12,8 V14 M12,17 V18",
        ["Wrench"]="M14,3 Q10,6 13,11 L3,21 L7,23 L17,13 Q22,15 23,8 L19,11 L16,8 L19,3 Z",
        ["Color"]="M12,2 C4,10 2,12 4,17 C8,25 22,21 21,14 C20,10 16,7 12,2 Z",
        ["Globe"]="M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M2,12 H22 M12,2 C5,8 5,16 12,22 C19,16 19,8 12,2",
        ["Key"]="M11,8 A5,5 0 1 1 1,8 A5,5 0 1 1 11,8 M10,11 L21,22 M16,17 L20,13 M19,20 L23,16",
        ["Brain"]="M12,5 C8,-1 3,3 4,8 C-1,11 1,18 5,18 C5,24 12,24 12,19 V5 C16,-1 21,3 20,8 C25,11 23,18 19,18 C19,24 12,24 12,19 M5,9 L9,12 M19,9 L15,12",
        ["Open"]="M12,3 H21 V12 M21,3 L10,14 M8,4 H3 V21 H20 V16",
        ["Share"]="M9,12 L17,6 M9,12 L17,18 M9,12 A3,3 0 1 1 3,12 A3,3 0 1 1 9,12 M22,4 A3,3 0 1 1 16,4 A3,3 0 1 1 22,4 M22,20 A3,3 0 1 1 16,20 A3,3 0 1 1 22,20",
        ["PanelRight"]="M2,3 H22 V21 H2 Z M15,3 V21",
        ["PanelLeft"]="M2,3 H22 V21 H2 Z M9,3 V21",
        ["PanelBottom"]="M2,3 H22 V21 H2 Z M2,15 H22",
        ["Search"]="M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9 M14,14 L22,22",
        ["ArrowLeft"]="M21,12 H3 M10,5 L3,12 L10,19", ["ArrowRight"]="M3,12 H21 M14,5 L21,12 L14,19",
        ["Archive"]="M2,3 H22 V8 H2 Z M4,8 V21 H20 V8 M9,12 H15",
        ["Pin"]="M7,3 H17 L15,10 L19,14 H5 L9,10 Z M12,14 V22",
        ["Branch"]="M6,5 V18 M18,6 V9 Q18,13 6,13 M9,3 A3,3 0 1 1 3,3 A3,3 0 1 1 9,3 M9,21 A3,3 0 1 1 3,21 A3,3 0 1 1 9,21 M21,3 A3,3 0 1 1 15,3 A3,3 0 1 1 21,3",
        ["ArrowUndo"]="M4,12 Q20,0 21,17 M4,5 V12 H11",
        ["ArrowRedo"]="M20,12 Q4,0 3,17 M20,5 V12 H13",
        ["Alert"]="M4,18 H20 L18,14 V9 C18,1 6,1 6,9 V14 Z M9,21 Q12,25 15,21",
        ["Attach"]="M9,14 L17,6 C21,2 25,6 21,10 L10,21 C3,28 -4,21 3,14 L15,2",
        ["ArrowDownload"]="M12,2 V16 M6,10 L12,16 L18,10 M3,18 V22 H21 V18",
        ["ArrowUpload"]="M12,16 V2 M6,8 L12,2 L18,8 M3,18 V22 H21 V18",
        ["Calendar"]="M3,5 H21 V22 H3 Z M3,10 H21 M8,2 V7 M16,2 V7 M7,14 H9 M14,14 H16 M7,18 H9",
        ["WindowNew"]="M3,4 H21 V20 H3 Z M3,9 H21 M15,11 V17 M12,14 H18",
        ["Code"]="M8,5 L2,12 L8,19 M16,5 L22,12 L16,19 M14,3 L10,21",
        ["ZoomIn"]="M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9 M14,14 L22,22 M5,9 H13 M9,5 V13",
        ["ZoomOut"]="M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9 M14,14 L22,22 M5,9 H13",
        ["Cut"]="M8,17 L21,2 M3,2 L21,20 M9,20 A4,4 0 1 1 1,20 A4,4 0 1 1 9,20 M23,20 A4,4 0 1 1 15,20 A4,4 0 1 1 23,20",
        ["ClipboardPaste"]="M8,4 H4 V22 H20 V4 H16 M8,2 H16 V7 H8 Z M8,12 H16 M8,16 H16",
        ["Keyboard"]="M2,5 H22 V20 H2 Z M5,9 H7 M11,9 H13 M17,9 H19 M5,13 H7 M11,13 H13 M17,13 H19 M6,17 H18",
        ["FullScreenMaximize"]="M3,9 V3 H9 M15,3 H21 V9 M21,15 V21 H15 M9,21 H3 V15",
        ["ArrowReply"]="M9,3 L2,10 L9,17 V12 Q19,10 22,20 Q22,5 9,7 Z",
        ["Terminal"]="M2,3 H22 V21 H2 Z M6,8 L10,12 L6,16 M13,16 H18",
        ["Automation"]="M12,2 L4,13 H11 L10,22 L20,9 H13 Z"
    };

    internal static ResourceDictionary Create(SkinId skin)
    {
        var strokeWidth = skin switch
        {
            SkinId.Metro => 2.0,
            SkinId.Aero => 1.4,
            SkinId.Luna => 1.6,
            _ => 1.8,
        };
        var pen = new Pen(Brushes.Black, strokeWidth)
        {
            LineCap = skin == SkinId.Metro ? PenLineCap.Square : PenLineCap.Round,
            LineJoin = skin == SkinId.Metro ? PenLineJoin.Miter : PenLineJoin.Round
        };
        var avatarCorner = skin switch
        {
            SkinId.Metro => 0.0,
            SkinId.Aero or SkinId.Luna => 3.0,
            _ => 20.0,
        };
        var terminalSelection = skin switch
        {
            SkinId.Metro => "#4DB2FF",
            SkinId.Aero => "#3399FF",
            SkinId.Luna => "#316AC5",
            _ => "#D0BCFF",
        };
        var resources = new ResourceDictionary();
        resources["AppSkinIconMonochrome"] = true;
        resources["AppCornerAvatarUser"] = new Avalonia.CornerRadius(avatarCorner);
        resources["AppCornerAvatarAssistant"] = new Avalonia.CornerRadius(avatarCorner);
        resources["AppTerminalSelection"] = new SolidColorBrush(Color.Parse(terminalSelection), .4);
        foreach (var (key, path) in Paths)
            resources["Icon." + key] = Geometry.Parse(path).GetWidenedGeometry(pen);
        return resources;
    }
}
