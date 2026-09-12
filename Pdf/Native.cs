using System;
using System.Runtime.InteropServices;

namespace PDFEditor.Pdf;

/// <summary>FS_RECTF de PDFium (attention : ordre gauche, haut, droite, bas).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsRectF
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsSizeF
{
    public float Width;
    public float Height;
}

/// <summary>FPDF_FILEACCESS : lecture d'un bloc de donnees par PDFium.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct FpdfFileAccess
{
    [FieldOffset(0)] public uint FileLen;
    [FieldOffset(8)] public IntPtr GetBlock;
    [FieldOffset(16)] public IntPtr Param;
}

/// <summary>
/// Liaison P/Invoke avec PDFium (paquet bblanchon.PDFium.Win32).
/// Les signatures suivent les en-tetes C livres avec le paquet
/// (build/native/include/pdfium). PDFium n'est pas reentrant :
/// tout appel doit se faire sous <see cref="PdfDoc.Sync"/>.
/// </summary>
internal static unsafe class Native
{
    private const string Lib = "pdfium";

    // ------------------------------------------------------------ constantes

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;

    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;

    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;

    public const int FPDF_FILLMODE_NONE = 0;
    public const int FPDF_FILLMODE_ALTERNATE = 1;
    public const int FPDF_FILLMODE_WINDING = 2;

    public const int FPDF_FONT_TRUETYPE = 2;

    public const int FPDF_TEXTRENDERMODE_INVISIBLE = 3;

    public const int FPDF_LINECAP_ROUND = 1;
    public const int FPDF_LINEJOIN_ROUND = 1;

    public const uint FPDF_NO_INCREMENTAL = 2;
    public const uint FPDF_REMOVE_SECURITY = 3;

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_WIDGET = 20;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDFANNOT_COLORTYPE_Color = 0;

    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    // ------------------------------------------------------------ fpdfview.h

    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr dataBuf, nuint size, byte* password);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetFileVersion(IntPtr doc, out int fileVersion);
    [DllImport(Lib)] public static extern uint FPDF_GetDocPermissions(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetSecurityHandlerRevision(IntPtr document);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, out FsSizeF size);
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ------------------------------------------------------------ fpdf_text.h

    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_IsGenerated(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern double FPDFText_GetFontSize(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern uint FPDFText_GetFontInfo(IntPtr textPage, int index, byte* buffer, uint buflen, out int flags);
    [DllImport(Lib)] public static extern int FPDFText_GetFontWeight(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_GetFillColor(IntPtr textPage, int index, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out FsRectF rect);
    [DllImport(Lib)] public static extern int FPDFText_GetCharOrigin(IntPtr textPage, int index, out double x, out double y);

    // ------------------------------------------------------------ fpdf_edit.h

    [DllImport(Lib)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern IntPtr FPDFPage_New(IntPtr document, int pageIndex, double width, double height);
    [DllImport(Lib)] public static extern void FPDFPage_Delete(IntPtr document, int pageIndex);
    [DllImport(Lib)] public static extern int FPDF_MovePages(IntPtr document, int* pageIndices, uint pageIndicesLen, int destPageIndex);
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Lib)] public static extern void FPDFPage_InsertObject(IntPtr page, IntPtr pageObj);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObj);
    [DllImport(Lib)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr pageObj);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);
    [DllImport(Lib)] public static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern void FPDFPageObj_SetBlendMode(IntPtr pageObject, [MarshalAs(UnmanagedType.LPStr)] string blendMode);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr pageObject, float width);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineJoin(IntPtr pageObject, int lineJoin);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineCap(IntPtr pageObject, int lineCap);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetDashArray(IntPtr pageObject, float* dashArray, nuint dashCount, float phase);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);
    [DllImport(Lib)] public static extern int FPDFPath_Close(IntPtr path);
    [DllImport(Lib)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewTextObj(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font, float fontSize);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);
    [DllImport(Lib)] public static extern int FPDFText_SetText(IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadFont(IntPtr document, byte* data, uint size, int fontType, int cid);
    [DllImport(Lib)] public static extern void FPDFFont_Close(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFTextObj_SetTextRenderMode(IntPtr text, int renderMode);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetTextRenderMode(IntPtr text);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, out FS_MATRIX matrix);
    [DllImport(Lib)] public static extern int FPDFFormObj_CountObjects(IntPtr formObject);
    [DllImport(Lib)] public static extern IntPtr FPDFFormObj_GetObject(IntPtr formObject, uint index);
    [DllImport(Lib)] public static extern int FPDFFormObj_RemoveObject(IntPtr formObject, IntPtr pageObject);

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX
    {
        public float A;
        public float B;
        public float C;
        public float D;
        public float E;
        public float F;
    }
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObject, IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFImageObj_LoadJpegFileInline(IntPtr* pages, int count, IntPtr imageObject, FpdfFileAccess* fileAccess);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetMatrix(IntPtr imageObject, double a, double b, double c, double d, double e, double f);

    // ------------------------------------------------------------ fpdf_ppo.h / fpdf_save.h

    [DllImport(Lib)] public static extern int FPDF_ImportPagesByIndex(IntPtr destDoc, IntPtr srcDoc, int* pageIndices, uint length, int index);
    [DllImport(Lib)] public static extern IntPtr FPDF_ImportNPagesToOne(IntPtr srcDoc, float outputWidth, float outputHeight, nuint numPagesOnXAxis, nuint numPagesOnYAxis);
    [DllImport(Lib)] public static extern int FPDF_CopyViewerPreferences(IntPtr destDoc, IntPtr srcDoc);
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr document, IntPtr fileWrite, uint flags);

    // ------------------------------------------------------------ fpdf_doc.h

    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte* buffer, uint buflen);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Lib)] public static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Lib)] public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [DllImport(Lib)] public static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, byte* buffer, uint buflen);
    [DllImport(Lib)] public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
    [DllImport(Lib)] public static extern uint FPDF_GetMetaText(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string tag, byte* buffer, uint buflen);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetAction(IntPtr link);

    // ------------------------------------------------------------ fpdf_transformpage.h

    [DllImport(Lib)] public static extern void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);

    // ------------------------------------------------------------ fpdf_annot.h

    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, ref FsRectF rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetURI(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string uri);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);

    // ------------------------------------------------------------ fpdf_formfill.h

    [DllImport(Lib)] public static extern int FPDF_GetFormType(IntPtr document);
    [DllImport(Lib)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);
    [DllImport(Lib)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport(Lib)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport(Lib)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport(Lib)] public static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern void FPDF_SetFormFieldHighlightColor(IntPtr form, int fieldType, uint color);
    [DllImport(Lib)] public static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr form, byte alpha);
    [DllImport(Lib)] public static extern void FPDF_RemoveFormFieldHighlight(IntPtr form);
    [DllImport(Lib)] public static extern int FPDFPage_HasFormFieldAtPoint(IntPtr form, IntPtr page, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnMouseMove(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnLButtonDown(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnLButtonUp(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnLButtonDoubleClick(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnKeyDown(IntPtr form, IntPtr page, int keyCode, int modifier);
    [DllImport(Lib)] public static extern int FORM_OnKeyUp(IntPtr form, IntPtr page, int keyCode, int modifier);
    [DllImport(Lib)] public static extern int FORM_OnChar(IntPtr form, IntPtr page, int ch, int modifier);
    [DllImport(Lib)] public static extern int FORM_ForceToKillFocus(IntPtr form);
    [DllImport(Lib)] public static extern int FORM_SelectAllText(IntPtr form, IntPtr page);
    [DllImport(Lib)] public static extern int FORM_GetFocusedAnnot(IntPtr form, out int pageIndex, out IntPtr annot);
}
