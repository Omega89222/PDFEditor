using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PDFEditor.Pdf;

/// <summary>
/// Document PDF ouvert dans PDFium.
///
/// PDFium n'etant pas reentrant, TOUTES les operations passent par le verrou
/// global <see cref="Sync"/> : le rendu en arriere-plan, l'interface et l'export
/// peuvent ainsi appeler cette classe depuis n'importe quel fil.
///
/// Les pages chargees sont gardees en cache ; toute operation structurelle
/// (suppression, insertion, deplacement, rotation) vide ce cache.
/// </summary>
public sealed unsafe class PdfDoc : IDisposable
{
    /// <summary>Verrou global de PDFium.</summary>
    public static readonly object Sync = new();

    private const int MaxCachedPages = 48;

    private static bool _libraryInitialized;

    private readonly Dictionary<int, IntPtr> _pages = new();
    private readonly Dictionary<int, PdfPageInfo> _infos = new();
    private readonly Dictionary<int, PdfTextPage> _texts = new();
    private readonly List<int> _pageUse = new();

    // Polices TrueType incorporees, partagees par toutes les pages : sans ce
    // cache, chaque page modifiee embarquerait une nouvelle copie du fichier.
    private readonly Dictionary<string, IntPtr> _fonts = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _doc;
    private IntPtr _buffer;
    private IntPtr _form;
    private IntPtr _formInfo;
    private int _focusedFormPage = -1;
    private bool _disposed;

    private PdfDoc(IntPtr doc, IntPtr buffer)
    {
        _doc = doc;
        _buffer = buffer;

        Native.FPDF_GetFileVersion(doc, out var version);
        FileVersion = version;
        FormType = Native.FPDF_GetFormType(doc);
        SecurityRevision = Native.FPDF_GetSecurityHandlerRevision(doc);
        Permissions = Native.FPDF_GetDocPermissions(doc);

        InitializeForms();
    }

    /// <summary>Version du fichier (14 = PDF 1.4...).</summary>
    public int FileVersion { get; }

    /// <summary>0 = aucun formulaire, 1 = AcroForm, 2/3 = XFA.</summary>
    public int FormType { get; }

    /// <summary>Revision du gestionnaire de securite, -1 si le fichier n'est pas chiffre.</summary>
    public int SecurityRevision { get; }

    public bool IsEncrypted => SecurityRevision >= 0;

    public bool HasForms => FormType > 0;

    /// <summary>Permissions (bits de la norme PDF) ; 0xFFFFFFFF si le fichier n'est pas chiffre.</summary>
    public uint Permissions { get; }

    /// <summary>
    /// Taille affichee d'une page (rotation appliquee) sans charger son contenu :
    /// indispensable pour ouvrir rapidement les documents de plusieurs centaines de pages.
    /// </summary>
    public Size GetPageSize(int index)
    {
        lock (Sync)
        {
            ThrowIfDisposed();
            if (_infos.TryGetValue(index, out var info))
            {
                return new Size(info.Width, info.Height);
            }

            return Native.FPDF_GetPageSizeByIndexF(_doc, index, out var size) != 0 && size.Width > 0.5 && size.Height > 0.5
                ? new Size(size.Width, size.Height)
                : new Size(612, 792);
        }
    }

    // =====================================================================
    // Ouverture / creation
    // =====================================================================

    public static void EnsureLibrary()
    {
        lock (Sync)
        {
            if (!_libraryInitialized)
            {
                Native.FPDF_InitLibrary();
                _libraryInitialized = true;
            }
        }
    }

    /// <summary>
    /// Ouvre un PDF depuis sa representation binaire. Leve
    /// <see cref="PdfPasswordException"/> si un mot de passe est requis.
    /// </summary>
    public static PdfDoc Open(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        EnsureLibrary();

        lock (Sync)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            Marshal.Copy(data, 0, buffer, data.Length);

            var doc = Load(buffer, data.Length, password, Encoding.UTF8);
            if (doc == IntPtr.Zero && !string.IsNullOrEmpty(password) && password.Any(c => c > 127))
            {
                // Anciens chiffrements (RC4 / AES-128) : mot de passe en Latin-1.
                doc = Load(buffer, data.Length, password, Encoding.Latin1);
            }

            if (doc == IntPtr.Zero)
            {
                var error = Native.FPDF_GetLastError();
                Marshal.FreeHGlobal(buffer);

                if (error == Native.FPDF_ERR_PASSWORD)
                {
                    throw new PdfPasswordException(!string.IsNullOrEmpty(password));
                }

                throw PdfLoadException.FromError(error);
            }

            return new PdfDoc(doc, buffer);
        }
    }

    private static IntPtr Load(IntPtr buffer, int length, string? password, Encoding encoding)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Native.FPDF_LoadMemDocument64(buffer, (nuint)length, null);
        }

        var bytes = encoding.GetBytes(password + "\0");
        fixed (byte* p = bytes)
        {
            return Native.FPDF_LoadMemDocument64(buffer, (nuint)length, p);
        }
    }

    /// <summary>Cree un document vide (sans aucune page).</summary>
    public static PdfDoc CreateEmpty()
    {
        EnsureLibrary();
        lock (Sync)
        {
            var doc = Native.FPDF_CreateNewDocument();
            if (doc == IntPtr.Zero)
            {
                throw new PdfLoadException("Impossible de créer un document PDF.");
            }

            return new PdfDoc(doc, IntPtr.Zero);
        }
    }

    /// <summary>Copie independante (enregistrement puis relecture, sans chiffrement).</summary>
    public PdfDoc Clone() => Open(Save());

    private void InitializeForms()
    {
        // Structure FPDF_FORMFILLINFO entierement a zero (aucun rappel) :
        // PDFium verifie chaque pointeur avant de l'utiliser.
        _formInfo = Marshal.AllocHGlobal(1024);
        new Span<byte>((void*)_formInfo, 1024).Clear();
        Marshal.WriteInt32(_formInfo, 0, 1);

        _form = Native.FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        SetFormHighlight(true);
    }

    // =====================================================================
    // Pages : cache et geometrie
    // =====================================================================

    public int PageCount
    {
        get
        {
            lock (Sync)
            {
                ThrowIfDisposed();
                return Native.FPDF_GetPageCount(_doc);
            }
        }
    }

    private IntPtr GetPage(int index)
    {
        ThrowIfDisposed();

        if (_pages.TryGetValue(index, out var page))
        {
            TouchPage(index);
            return page;
        }

        if (index < 0 || index >= Native.FPDF_GetPageCount(_doc))
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Page {index + 1} inexistante.");
        }

        page = Native.FPDF_LoadPage(_doc, index);
        if (page == IntPtr.Zero)
        {
            throw new PdfLoadException($"La page {index + 1} n’a pas pu être chargée.");
        }

        if (_form != IntPtr.Zero)
        {
            Native.FORM_OnAfterLoadPage(page, _form);
        }

        _pages[index] = page;
        TouchPage(index);
        TrimPageCache(index);
        return page;
    }

    private void TouchPage(int index)
    {
        _pageUse.Remove(index);
        _pageUse.Add(index);
    }

    private void TrimPageCache(int keep)
    {
        while (_pages.Count > MaxCachedPages && _pageUse.Count > 0)
        {
            var victim = _pageUse[0];
            if (victim == keep || victim == _focusedFormPage)
            {
                _pageUse.RemoveAt(0);
                _pageUse.Add(victim);
                if (_pageUse.All(i => i == keep || i == _focusedFormPage))
                {
                    break;
                }

                continue;
            }

            ClosePageHandle(victim);
        }
    }

    private void ClosePageHandle(int index)
    {
        _pageUse.Remove(index);
        if (_pages.Remove(index, out var page))
        {
            if (_form != IntPtr.Zero)
            {
                Native.FORM_OnBeforeClosePage(page, _form);
            }

            Native.FPDF_ClosePage(page);
        }
    }

    /// <summary>Oublie tout ce qui concerne une page (apres modification).</summary>
    private void InvalidatePage(int index)
    {
        if (index == _focusedFormPage && _form != IntPtr.Zero)
        {
            Native.FORM_ForceToKillFocus(_form);
            _focusedFormPage = -1;
        }

        ClosePageHandle(index);
        _infos.Remove(index);
        _texts.Remove(index);
    }

    /// <summary>Vide tous les caches (operations structurelles).</summary>
    private void InvalidateAll()
    {
        if (_form != IntPtr.Zero)
        {
            Native.FORM_ForceToKillFocus(_form);
        }

        _focusedFormPage = -1;
        foreach (var index in _pages.Keys.ToList())
        {
            ClosePageHandle(index);
        }

        _pages.Clear();
        _pageUse.Clear();
        _infos.Clear();
        _texts.Clear();
    }

    /// <summary>
    /// Geometrie de la page. La matrice espace utilisateur -> affichage est
    /// deduite de FPDF_PageToDevice sur trois points, a une echelle de 1000 :
    /// on reste ainsi exactement aligne sur le rendu de PDFium (CropBox,
    /// rotation, origine decalee de la MediaBox...).
    /// </summary>
    public PdfPageInfo GetPageInfo(int index)
    {
        lock (Sync)
        {
            if (_infos.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var page = GetPage(index);
            double width = Native.FPDF_GetPageWidthF(page);
            double height = Native.FPDF_GetPageHeightF(page);
            width = width > 0.5 ? width : 612;
            height = height > 0.5 ? height : 792;

            const double k = 1000;
            var sizeX = (int)Math.Round(width * k);
            var sizeY = (int)Math.Round(height * k);
            var sx = sizeX / width;
            var sy = sizeY / height;

            Point Probe(double x, double y)
            {
                Native.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, x, y, out var dx, out var dy);
                return new Point(dx / sx, dy / sy);
            }

            var o = Probe(0, 0);
            var px = Probe(100, 0);
            var py = Probe(0, 100);

            static double Snap(double v) => Math.Abs(v - Math.Round(v)) < 0.02 ? Math.Round(v) : v;

            var toDisplay = new PdfMatrix(
                Snap((px.X - o.X) / 100),
                Snap((px.Y - o.Y) / 100),
                Snap((py.X - o.X) / 100),
                Snap((py.Y - o.Y) / 100),
                Math.Round(o.X, 3),
                Math.Round(o.Y, 3));

            var info = new PdfPageInfo
            {
                Index = index,
                Width = width,
                Height = height,
                Rotation = Native.FPDFPage_GetRotation(page),
                UserToDisplay = toDisplay,
                DisplayToUser = toDisplay.Invert()
            };

            _infos[index] = info;
            return info;
        }
    }

    // =====================================================================
    // Rendu
    // =====================================================================

    /// <summary>
    /// Rend une zone de la page dans un tampon BGRA (fond blanc).
    /// La page complete mesurerait <c>Width * scale</c> x <c>Height * scale</c>
    /// pixels ; le tampon en couvre la portion commencant a (offsetX, offsetY).
    /// </summary>
    public byte[] Render(int index, int bitmapWidth, int bitmapHeight, double scale, int offsetX = 0, int offsetY = 0, bool printing = false,
        IReadOnlyList<Rect>? hiddenText = null)
    {
        bitmapWidth = Math.Max(1, bitmapWidth);
        bitmapHeight = Math.Max(1, bitmapHeight);
        var pixels = new byte[bitmapWidth * bitmapHeight * 4];

        lock (Sync)
        {
            var page = GetPage(index);
            var info = GetPageInfo(index);
            var fullWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
            var fullHeight = Math.Max(1, (int)Math.Round(info.Height * scale));
            var flags = Native.FPDF_ANNOT | (printing ? Native.FPDF_PRINTING : 0);

            // Lignes modifiees : le texte d'origine est rendu invisible le temps du rendu,
            // le fond de la page (couleurs, images) reste donc exactement le meme.
            var hidden = new List<(IntPtr Object, int Mode)>();
            if (hiddenText is { Count: > 0 })
            {
                foreach (var match in TextRegions.Find(page, info, hiddenText))
                {
                    var mode = Native.FPDFTextObj_GetTextRenderMode(match.Object);
                    if (mode is 0 or 1 or 2 && Native.FPDFTextObj_SetTextRenderMode(match.Object, Native.FPDF_TEXTRENDERMODE_INVISIBLE) != 0)
                    {
                        hidden.Add((match.Object, mode));
                    }
                }
            }

            try
            {
                fixed (byte* p = pixels)
                {
                    var bitmap = Native.FPDFBitmap_CreateEx(bitmapWidth, bitmapHeight, Native.FPDFBitmap_BGRA, (IntPtr)p, bitmapWidth * 4);
                    if (bitmap == IntPtr.Zero)
                    {
                        return pixels;
                    }

                    try
                    {
                        Native.FPDFBitmap_FillRect(bitmap, 0, 0, bitmapWidth, bitmapHeight, 0xFFFFFFFF);
                        Native.FPDF_RenderPageBitmap(bitmap, page, -offsetX, -offsetY, fullWidth, fullHeight, 0, flags);

                        if (_form != IntPtr.Zero)
                        {
                            Native.FPDF_FFLDraw(_form, bitmap, page, -offsetX, -offsetY, fullWidth, fullHeight, 0, flags);
                        }
                    }
                    finally
                    {
                        Native.FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
            finally
            {
                foreach (var (obj, mode) in hidden)
                {
                    Native.FPDFTextObj_SetTextRenderMode(obj, mode);
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Vrai si la zone contient du vrai texte PDF visible, que l'on peut retirer sans toucher au fond.
    /// Faux pour une page numerisee (texte dans l'image, couche OCR invisible).
    /// </summary>
    public bool HasVisibleText(int index, Rect display)
    {
        lock (Sync)
        {
            var page = GetPage(index);
            var info = GetPageInfo(index);
            return TextRegions.Find(page, info, new[] { display }).Any(m => TextRegions.IsVisible(m.Object));
        }
    }

    // =====================================================================
    // Texte, signets, liens, metadonnees
    // =====================================================================

    public PdfTextPage GetTextPage(int index)
    {
        lock (Sync)
        {
            if (_texts.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var page = GetPage(index);
            var info = GetPageInfo(index);
            var textPage = Native.FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero)
            {
                return PdfTextPage.Empty(index);
            }

            try
            {
                var result = PdfTextPage.Build(index, textPage, info);
                _texts[index] = result;
                return result;
            }
            finally
            {
                Native.FPDFText_ClosePage(textPage);
            }
        }
    }

    public List<PdfOutlineItem> GetOutline()
    {
        lock (Sync)
        {
            ThrowIfDisposed();
            var root = new List<PdfOutlineItem>();
            var budget = 5000;
            var seen = new HashSet<IntPtr>();
            ReadBookmarks(IntPtr.Zero, root, 0, ref budget, seen);
            return root;
        }
    }

    private void ReadBookmarks(IntPtr parent, List<PdfOutlineItem> into, int depth, ref int budget, HashSet<IntPtr> seen)
    {
        if (depth > 16)
        {
            return;
        }

        var child = Native.FPDFBookmark_GetFirstChild(_doc, parent);
        while (child != IntPtr.Zero && budget-- > 0 && seen.Add(child))
        {
            var item = new PdfOutlineItem
            {
                Title = ReadBookmarkTitle(child),
                PageIndex = ResolveBookmarkPage(child)
            };

            ReadBookmarks(child, item.Children, depth + 1, ref budget, seen);
            into.Add(item);
            child = Native.FPDFBookmark_GetNextSibling(_doc, child);
        }
    }

    private static string ReadBookmarkTitle(IntPtr bookmark)
    {
        var length = Native.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (length <= 2)
        {
            return "";
        }

        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            Native.FPDFBookmark_GetTitle(bookmark, p, length);
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)length - 2).Trim();
    }

    private int ResolveBookmarkPage(IntPtr bookmark)
    {
        var dest = Native.FPDFBookmark_GetDest(_doc, bookmark);
        if (dest == IntPtr.Zero)
        {
            var action = Native.FPDFBookmark_GetAction(bookmark);
            if (action != IntPtr.Zero && Native.FPDFAction_GetType(action) == Native.PDFACTION_GOTO)
            {
                dest = Native.FPDFAction_GetDest(_doc, action);
            }
        }

        return dest == IntPtr.Zero ? -1 : Native.FPDFDest_GetDestPageIndex(_doc, dest);
    }

    public PdfMetadata GetMetadata()
    {
        lock (Sync)
        {
            ThrowIfDisposed();
            return new PdfMetadata
            {
                Title = ReadMeta("Title"),
                Author = ReadMeta("Author"),
                Subject = ReadMeta("Subject"),
                Keywords = ReadMeta("Keywords"),
                Creator = ReadMeta("Creator"),
                Producer = ReadMeta("Producer"),
                Created = PdfMetadata.ParsePdfDate(ReadMeta("CreationDate")),
                Modified = PdfMetadata.ParsePdfDate(ReadMeta("ModDate"))
            };
        }
    }

    private string ReadMeta(string tag)
    {
        var length = Native.FPDF_GetMetaText(_doc, tag, null, 0);
        if (length <= 2)
        {
            return "";
        }

        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            Native.FPDF_GetMetaText(_doc, tag, p, length);
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)length - 2).Trim();
    }

    /// <summary>Lien situe sous un point d'affichage de la page, ou null.</summary>
    public PdfLinkTarget? GetLinkAt(int index, Point display)
    {
        lock (Sync)
        {
            var page = GetPage(index);
            var info = GetPageInfo(index);
            var user = info.DisplayToUser.Transform(display);
            var link = Native.FPDFLink_GetLinkAtPoint(page, user.X, user.Y);
            if (link == IntPtr.Zero)
            {
                return null;
            }

            var dest = Native.FPDFLink_GetDest(_doc, link);
            if (dest != IntPtr.Zero)
            {
                return new PdfLinkTarget { PageIndex = Native.FPDFDest_GetDestPageIndex(_doc, dest) };
            }

            var action = Native.FPDFLink_GetAction(link);
            if (action == IntPtr.Zero)
            {
                return null;
            }

            switch (Native.FPDFAction_GetType(action))
            {
                case Native.PDFACTION_GOTO:
                    dest = Native.FPDFAction_GetDest(_doc, action);
                    return dest == IntPtr.Zero ? null : new PdfLinkTarget { PageIndex = Native.FPDFDest_GetDestPageIndex(_doc, dest) };

                case Native.PDFACTION_URI:
                    var length = Native.FPDFAction_GetURIPath(_doc, action, null, 0);
                    if (length <= 1)
                    {
                        return null;
                    }

                    var buffer = new byte[length];
                    fixed (byte* p = buffer)
                    {
                        Native.FPDFAction_GetURIPath(_doc, action, p, length);
                    }

                    return new PdfLinkTarget { Uri = Encoding.UTF8.GetString(buffer, 0, (int)length - 1) };
            }

            return null;
        }
    }

    // =====================================================================
    // Formulaires interactifs
    // =====================================================================

    public void SetFormHighlight(bool enabled)
    {
        lock (Sync)
        {
            if (_form == IntPtr.Zero)
            {
                return;
            }

            if (enabled)
            {
                // 0x00RRGGBB : jaune pale, dans l'esprit de l'accent de l'app.
                Native.FPDF_SetFormFieldHighlightColor(_form, 0, 0x00FFE38A);
                Native.FPDF_SetFormFieldHighlightAlpha(_form, 90);
            }
            else
            {
                Native.FPDF_RemoveFormFieldHighlight(_form);
            }
        }
    }

    /// <summary>Type du champ de formulaire sous le point, ou -1.</summary>
    public int GetFormFieldTypeAt(int index, Point display)
    {
        lock (Sync)
        {
            if (_form == IntPtr.Zero || !HasForms)
            {
                return -1;
            }

            var page = GetPage(index);
            var user = GetPageInfo(index).DisplayToUser.Transform(display);
            return Native.FPDFPage_HasFormFieldAtPoint(_form, page, user.X, user.Y);
        }
    }

    public bool FormMouseMove(int index, Point display, int modifiers) =>
        FormPointer(index, display, modifiers, 0);

    public bool FormMouseDown(int index, Point display, int modifiers) =>
        FormPointer(index, display, modifiers, 1);

    public bool FormMouseUp(int index, Point display, int modifiers) =>
        FormPointer(index, display, modifiers, 2);

    public bool FormDoubleClick(int index, Point display, int modifiers) =>
        FormPointer(index, display, modifiers, 3);

    private bool FormPointer(int index, Point display, int modifiers, int kind)
    {
        lock (Sync)
        {
            if (_form == IntPtr.Zero)
            {
                return false;
            }

            var page = GetPage(index);
            var user = GetPageInfo(index).DisplayToUser.Transform(display);
            var result = kind switch
            {
                0 => Native.FORM_OnMouseMove(_form, page, modifiers, user.X, user.Y),
                1 => Native.FORM_OnLButtonDown(_form, page, modifiers, user.X, user.Y),
                2 => Native.FORM_OnLButtonUp(_form, page, modifiers, user.X, user.Y),
                _ => Native.FORM_OnLButtonDoubleClick(_form, page, modifiers, user.X, user.Y)
            };

            if (kind is 1 or 3)
            {
                _focusedFormPage = QueryFocusedFormPage();
            }

            return result != 0;
        }
    }

    public bool FormKeyDown(int index, int virtualKey, int modifiers)
    {
        lock (Sync)
        {
            return _form != IntPtr.Zero && Native.FORM_OnKeyDown(_form, GetPage(index), virtualKey, modifiers) != 0;
        }
    }

    public bool FormKeyUp(int index, int virtualKey, int modifiers)
    {
        lock (Sync)
        {
            return _form != IntPtr.Zero && Native.FORM_OnKeyUp(_form, GetPage(index), virtualKey, modifiers) != 0;
        }
    }

    public bool FormChar(int index, int character, int modifiers)
    {
        lock (Sync)
        {
            return _form != IntPtr.Zero && Native.FORM_OnChar(_form, GetPage(index), character, modifiers) != 0;
        }
    }

    public void FormSelectAll(int index)
    {
        lock (Sync)
        {
            if (_form != IntPtr.Zero)
            {
                Native.FORM_SelectAllText(_form, GetPage(index));
            }
        }
    }

    /// <summary>Retire le focus du champ en cours (valide la saisie).</summary>
    public void FormKillFocus()
    {
        lock (Sync)
        {
            if (_form != IntPtr.Zero)
            {
                Native.FORM_ForceToKillFocus(_form);
            }

            _focusedFormPage = -1;
        }
    }

    /// <summary>Page du champ de formulaire qui a le focus, ou -1.</summary>
    public int FocusedFormPage
    {
        get
        {
            lock (Sync)
            {
                return _form == IntPtr.Zero ? -1 : QueryFocusedFormPage();
            }
        }
    }

    private int QueryFocusedFormPage()
    {
        if (Native.FORM_GetFocusedAnnot(_form, out var pageIndex, out var annot) == 0 || annot == IntPtr.Zero)
        {
            return -1;
        }

        Native.FPDFPage_CloseAnnot(annot);
        return pageIndex;
    }

    // =====================================================================
    // Operations sur les pages
    // =====================================================================

    public void DeletePage(int index)
    {
        lock (Sync)
        {
            InvalidateAll();
            Native.FPDFPage_Delete(_doc, index);
        }
    }

    public void InsertBlankPage(int index, double width, double height)
    {
        lock (Sync)
        {
            InvalidateAll();
            var page = Native.FPDFPage_New(_doc, index, width, height);
            if (page != IntPtr.Zero)
            {
                Native.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Insere des pages d'un autre document a la position donnee.</summary>
    public bool ImportPages(PdfDoc source, IReadOnlyList<int> sourceIndices, int destIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceIndices.Count == 0)
        {
            return true;
        }

        if (ReferenceEquals(source, this))
        {
            using var copy = Clone();
            return ImportPages(copy, sourceIndices, destIndex);
        }

        lock (Sync)
        {
            InvalidateAll();
            var indices = sourceIndices.ToArray();
            fixed (int* p = indices)
            {
                return Native.FPDF_ImportPagesByIndex(_doc, source._doc, p, (uint)indices.Length, destIndex) != 0;
            }
        }
    }

    /// <summary>
    /// Deplace des pages (semantique de FPDF_MovePages : <paramref name="destIndex"/>
    /// est la position de la premiere page deplacee dans le document resultant).
    /// </summary>
    public bool MovePages(IReadOnlyList<int> indices, int destIndex)
    {
        lock (Sync)
        {
            InvalidateAll();
            var array = indices.ToArray();
            fixed (int* p = array)
            {
                return Native.FPDF_MovePages(_doc, p, (uint)array.Length, destIndex) != 0;
            }
        }
    }

    public int GetRotation(int index)
    {
        lock (Sync)
        {
            return Native.FPDFPage_GetRotation(GetPage(index));
        }
    }

    public void SetRotation(int index, int quarterTurns)
    {
        lock (Sync)
        {
            var page = GetPage(index);
            Native.FPDFPage_SetRotation(page, ((quarterTurns % 4) + 4) % 4);
            InvalidatePage(index);
        }
    }

    /// <summary>Recadre la page sur un rectangle d'affichage.</summary>
    public void SetCropBox(int index, Rect display)
    {
        lock (Sync)
        {
            var page = GetPage(index);
            var (left, bottom, right, top) = GetPageInfo(index).ToUserRect(display);
            Native.FPDFPage_SetCropBox(page, (float)left, (float)bottom, (float)right, (float)top);
            InvalidatePage(index);
        }
    }

    /// <summary>
    /// Modifie le contenu d'une page (objets graphiques, texte, images,
    /// annotations). Le contenu est regenere puis la page rechargee.
    /// </summary>
    public void EditPage(int index, Action<PdfPageEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        lock (Sync)
        {
            if (_form != IntPtr.Zero)
            {
                Native.FORM_ForceToKillFocus(_form);
                _focusedFormPage = -1;
            }

            var page = GetPage(index);
            var info = GetPageInfo(index);

            using (var editor = new PdfPageEditor(_doc, page, info, _fonts))
            {
                edit(editor);
                editor.Commit();
            }

            InvalidatePage(index);
        }
    }

    /// <summary>Nouveau document assemblant N pages par feuille.</summary>
    public PdfDoc? CreateNUp(double sheetWidth, double sheetHeight, int columns, int rows)
    {
        lock (Sync)
        {
            ThrowIfDisposed();
            var doc = Native.FPDF_ImportNPagesToOne(_doc, (float)sheetWidth, (float)sheetHeight, (nuint)columns, (nuint)rows);
            return doc == IntPtr.Zero ? null : new PdfDoc(doc, IntPtr.Zero);
        }
    }

    /// <summary>Nouveau document contenant une copie des pages demandees.</summary>
    public PdfDoc ExtractPages(IReadOnlyList<int> indices)
    {
        var result = CreateEmpty();
        lock (Sync)
        {
            result.ImportPages(this, indices, 0);
            Native.FPDF_CopyViewerPreferences(result._doc, _doc);
        }

        return result;
    }

    // =====================================================================
    // Enregistrement
    // =====================================================================

    /// <summary>
    /// Serialise le document. Par defaut, le chiffrement eventuel est retire :
    /// il est reapplique ensuite par <see cref="PdfPostProcessor"/>.
    /// </summary>
    public byte[] Save(bool removeSecurity = true)
    {
        lock (Sync)
        {
            ThrowIfDisposed();

            if (_form != IntPtr.Zero)
            {
                Native.FORM_ForceToKillFocus(_form);
                _focusedFormPage = -1;
            }

            return PdfFileWriter.Save(_doc, removeSecurity ? Native.FPDF_REMOVE_SECURITY : Native.FPDF_NO_INCREMENTAL);
        }
    }

    public void SaveTo(string path, bool removeSecurity = true)
    {
        File.WriteAllBytes(path, Save(removeSecurity));
    }

    // =====================================================================
    // Liberation
    // =====================================================================

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfDoc));
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed)
            {
                return;
            }

            InvalidateAll();

            foreach (var font in _fonts.Values)
            {
                if (font != IntPtr.Zero)
                {
                    Native.FPDFFont_Close(font);
                }
            }

            _fonts.Clear();

            if (_form != IntPtr.Zero)
            {
                Native.FPDFDOC_ExitFormFillEnvironment(_form);
                _form = IntPtr.Zero;
            }

            if (_formInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_formInfo);
                _formInfo = IntPtr.Zero;
            }

            if (_doc != IntPtr.Zero)
            {
                Native.FPDF_CloseDocument(_doc);
                _doc = IntPtr.Zero;
            }

            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            _disposed = true;
        }
    }
}

/// <summary>Ecriture d'un document PDFium dans un tableau d'octets (FPDF_FILEWRITE).</summary>
internal static unsafe class PdfFileWriter
{
    // Toujours appele sous PdfDoc.Sync : un champ statique suffit.
    private static MemoryStream? _target;

    [UnmanagedCallersOnly]
    private static int WriteBlock(IntPtr self, byte* data, uint size)
    {
        try
        {
            _target?.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public static byte[] Save(IntPtr document, uint flags)
    {
        var stream = new MemoryStream();
        var writer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(writer, 0, 1);
            Marshal.WriteIntPtr(writer, 8, (IntPtr)(delegate* unmanaged<IntPtr, byte*, uint, int>)&WriteBlock);
            _target = stream;

            if (Native.FPDF_SaveAsCopy(document, writer, flags) == 0)
            {
                throw new IOException("PDFium n’a pas pu enregistrer le document.");
            }

            return stream.ToArray();
        }
        finally
        {
            _target = null;
            Marshal.FreeHGlobal(writer);
        }
    }
}
