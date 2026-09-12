using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFEditor.Pdf;

namespace PDFEditor.Services;

/// <summary>
/// File de rendu en arriere-plan (un fil dedie, PDFium etant sequentiel).
/// Une requete est identifiee par une cle : une nouvelle requete remplace
/// celle qui porte la meme cle. A priorite egale, la plus recente passe
/// d'abord (on rend en premier ce qui vient d'apparaitre a l'ecran).
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    public const int PriorityVisible = 0;
    public const int PriorityDetail = 1;
    public const int PriorityThumbnail = 2;
    public const int PriorityBackground = 3;

    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private PdfDoc? _document;
    private long _sequence;
    private volatile bool _stopping;

    public RenderScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Rendu PDF",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    private sealed class Job
    {
        public required object Key { get; init; }
        public int Priority { get; init; }
        public long Sequence { get; init; }
        public required Func<PdfDoc, BitmapSource?> Work { get; init; }
        public required Action<BitmapSource> Completed { get; init; }
    }

    /// <summary>Document rendu. Le remplacer vide la file.</summary>
    public PdfDoc? Document
    {
        get
        {
            lock (_gate)
            {
                return _document;
            }
        }
        set
        {
            lock (_gate)
            {
                _document = value;
                _jobs.Clear();
            }
        }
    }

    public void Submit(object key, int priority, Func<PdfDoc, BitmapSource?> work, Action<BitmapSource> completed)
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => Equals(j.Key, key));
            _jobs.Add(new Job
            {
                Key = key,
                Priority = priority,
                Sequence = ++_sequence,
                Work = work,
                Completed = completed
            });
        }

        _signal.Set();
    }

    public void Cancel(object key)
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => Equals(j.Key, key));
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            _jobs.Clear();
        }
    }

    private void Run()
    {
        while (!_stopping)
        {
            Job? job = null;
            PdfDoc? document;

            lock (_gate)
            {
                document = _document;
                foreach (var candidate in _jobs)
                {
                    if (job is null
                        || candidate.Priority < job.Priority
                        || (candidate.Priority == job.Priority && candidate.Sequence > job.Sequence))
                    {
                        job = candidate;
                    }
                }

                if (job is not null)
                {
                    _jobs.Remove(job);
                }
            }

            if (job is null || document is null)
            {
                _signal.WaitOne(250);
                continue;
            }

            try
            {
                var bitmap = job.Work(document);
                if (bitmap is not null && !_stopping)
                {
                    var completed = job.Completed;
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => completed(bitmap)));
                }
            }
            catch (ObjectDisposedException)
            {
                // Document ferme ou remplace pendant le rendu : sans importance.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RenderScheduler] {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
    }
}
