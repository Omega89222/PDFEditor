using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace PDFEditorSetup;

/// <summary>
/// Archive de l'application ajoutee a la fin de Setup.exe par build-installer.ps1 :
///   [Setup.exe][application.zip][longueur du zip : Int64][« PDFEDITOR-SETUP1 »]
/// Windows ignore ces octets en fin d'executable. Les octets qui precedent
/// l'archive forment le programme seul, reutilise comme desinstalleur.
/// </summary>
internal sealed class Payload
{
    private const int FooterSize = 24;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PDFEDITOR-SETUP1");

    private Payload(string path, long offset, long length)
    {
        ExecutablePath = path;
        Offset = offset;
        Length = length;
    }

    /// <summary>Chemin de l'executable en cours.</summary>
    public static string CurrentExecutable => Assembly.GetEntryAssembly()!.Location;

    public string ExecutablePath { get; }

    /// <summary>Position de l'archive = taille du programme seul.</summary>
    public long Offset { get; }

    public long Length { get; }

    /// <summary>Archive embarquee, ou null (desinstalleur, compilation de developpement).</summary>
    public static Payload? Find()
    {
        try
        {
            var path = CurrentExecutable;
            using var stream = OpenShared(path);
            if (stream.Length < FooterSize)
            {
                return null;
            }

            stream.Seek(-FooterSize, SeekOrigin.End);
            var footer = new byte[FooterSize];
            var read = 0;
            while (read < FooterSize)
            {
                var n = stream.Read(footer, read, FooterSize - read);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (footer[8 + i] != Magic[i])
                {
                    return null;
                }
            }

            var length = BitConverter.ToInt64(footer, 0);
            var offset = stream.Length - FooterSize - length;
            return length > 0 && offset > 0 ? new Payload(path, offset, length) : null;
        }
        catch (Exception ex)
        {
            Log.Write("Lecture de l'archive impossible : " + ex.Message);
            return null;
        }
    }

    public Stream OpenArchive() => new SubStream(OpenShared(ExecutablePath), Offset, Length);

    /// <summary>Ecrit le programme seul (sans archive) : il servira de desinstalleur.</summary>
    public void WriteProgramOnly(string destination)
    {
        using var input = OpenShared(ExecutablePath);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        var remaining = Offset;
        while (remaining > 0)
        {
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0)
            {
                throw new EndOfStreamException("Programme d'installation tronqué.");
            }

            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Fenetre en lecture seule sur une portion d'un flux.</summary>
    private sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SubStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Max(0, Math.Min(_length, value));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            _inner.Position = _start + _position;
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
