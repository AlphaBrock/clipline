using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;

namespace Clipline;

/// The transparent strip along the top of a screen, and when it shows.
///
/// Rest the pointer on the top edge and the line slides down on that screen.
/// Move away and it tucks itself back up. A new screenshot brings it down for
/// a moment, and Ctrl+Alt+T or the tray icon shows or hides it.
///
/// Only the rope and the photos are part of the window: the space between
/// them is cut out with a window region, so clicks fall through to the app
/// underneath.
///
/// The line has two sides, switched under it: the captures, and notes of
/// the text copied lately. Only one side hangs on the rope at a time. Pinned
/// ones hang first on either side. The notes can be searched: only those
/// that match stay on the rope.
public sealed partial class LineWindow : Window
{
    /// Tall enough for the tallest photo on a fully laden, sagging rope,
    /// and the switch between captures and copied text below it.
    private const double StripHeight = 350;
    /// How far either side of the rope still belongs to the window.
    private const double RopeReach = 5;
    /// The hint shown on an empty line hangs just under the rope.
    private const double HintTop = Rope.Top + 30;
    /// The photo height that weighs 1. Taller photos are heavier.
    private const double AverageCardHeight = 125;
    private const double CardGap = 28;
    private const double CardStep = PhotoCard.CardWidth + CardGap;
    /// Room kept free at both ends of the screen, for the "more" chips.
    private const double EndRoom = 150;
    /// One notch of a mouse wheel.
    private const int WheelNotch = 120;
    /// Room around the photos for their swing and shadow.
    private const double SwingRoom = 44;
    /// The ends of the top edge never bring the line down: maximised windows
    /// keep their caption buttons there.
    private const double DeadCorner = 160;
    /// Each half of the switch between captures and copied text.
    private const double SideTabWidth = 88;
    /// A note held up to read hangs just under the rope.
    private const double ReaderTop = Rope.Top + 18;
    private const string PhotosHint = "截个图试试 · Win+Shift+S 或 Win+PrtScn";
    private const string NotesHint = "复制的文字会夹在这里，点一下就能再复制";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FreshShotStay = TimeSpan.FromSeconds(2.6);
    /// How long the line stays out of sight before its memory is let go.
    private static readonly TimeSpan TrimDelay = TimeSpan.FromSeconds(3);
    /// How often what has been kept too long is cleared away.
    private static readonly TimeSpan ExpireInterval = TimeSpan.FromHours(1);

    private readonly IntPtr hwnd;
    private readonly DispatcherQueueTimer poll;
    private readonly DispatcherQueueTimer regionSettle;
    private readonly DispatcherQueueTimer trim;
    private readonly DispatcherQueueTimer expire;
    private readonly List<CaptureWatcher> watchers = new();
    private readonly ClipboardCapture clipboard = new();
    private readonly ClipboardText clipboardText = new();
    private readonly Shell shell;
    private readonly List<PhotoCard> cards = new();
    /// Notes of copied text, in the history's order. Only made while the
    /// line shows that side, and let go of once it has faded away.
    private readonly List<NoteCard> notes = new();
    /// The notes the search finds, in the same order: all of them when
    /// nothing is searched for. These are the ones that hang.
    private List<NoteCard> shownNotes = new();
    /// What the notes are searched for, if anything.
    private string query = "";
    /// Typing into the search box: the window has the keyboard for now.
    private bool typing;
    /// The window has the keyboard, for typing a search or for Esc to put
    /// back the note being read.
    private bool keyboardHeld;
    /// The window that had the keyboard before, to give it back after.
    private IntPtr typedOver;
    private readonly Rope rope = new();
    /// Every photo's and note's peg, including ones still falling away.
    private readonly Dictionary<UserControl, Rope.Peg> pegs = new();
    private enum Side { Photos, Notes }
    private Side side = Side.Photos;
    /// Where the side not showing was scrolled to.
    private int otherFirst;
    /// Where the note being read is, while it is.
    private Rect? readerArea;
    private readonly PolyLineSegment ropeLine = new(), haloLine = new(), shineLine = new();
    /// The simulation runs, a step every frame.
    private bool running;
    private TimeSpan? lastFrame;
    /// The window lets no clicks through while a photo is in the air.
    private bool wholeWindow;
    private bool wasUnsettled;
    // Where the pointer last was over the strip, when, and how fast it was going.
    private Point? lastPointer;
    private long lastPointerTicks;
    private Point pointerVelocity;
    /// The lowest the rope hangs, at rest or on its way there.
    private double ropeLowest = Rope.Top;

    private Monitor monitor;
    private bool shown;
    private bool hiding;
    /// Brought down by the shortcut, the tray or a new screenshot rather than
    /// by the pointer: it waits for the pointer to visit before leaving on
    /// its own.
    private bool summoned;
    private bool pointerVisited;
    private DateTime? hideAt;
    private DateTime? dwellingSince;
    private DateTime? outsideSince;
    private int drags;

    /// Newest first. Only `capacity` of them are in view, starting at `first`.
    private int first;
    private int capacity = Settings.Current.MaxVisible;
    private int wheel;

    /// Where the photos are, in window coordinates (DIPs). The pointer counts
    /// as being on the line inside it.
    private Rect photoArea;
    private Rect? previousRegionArea;
    /// Where the photos were before the line was paged along. The pointer
    /// that paged it is likely still there, maybe over a chip that just went
    /// away or a gap the shorter last page left: that still counts as being
    /// on the line, until the pointer leaves it.
    private Rect? pagedFrom;

    public LineWindow()
    {
        InitializeComponent();
        hwnd = WindowNative.GetWindowHandle(this);

        SystemBackdrop = new TransparentBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Native.MakeOverlay(hwnd);

        Strip.Height = StripHeight;
        EmptyHint.Margin = new Thickness(0, HintTop, 0, 0);
        rope.PlaceStrip(-StripHeight);
        Strip.Translation = new Vector3(0, (float)-StripHeight, 0);
        RopePath.Data = LineGeometry(ropeLine);
        RopeHalo.Data = LineGeometry(haloLine);
        RopeShine.Data = LineGeometry(shineLine);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        Root.PointerExited += (_, _) => lastPointer = null;

        Root.SizeChanged += (_, _) => Relayout();
        EmptyHint.SizeChanged += (_, _) => Relayout();
        Root.PointerWheelChanged += OnWheel;
        NewerChip.Tapped += (_, e) => { e.Handled = true; ScrollBy(-capacity); };
        OlderChip.Tapped += (_, e) => { e.Handled = true; ScrollBy(capacity); };
        PrepareSideSwitch();
        PrepareSearch();
        // A click on the line anywhere but the note being read puts it back.
        Root.Tapped += (_, _) => Reader.Close();
        Reader.Closed += () =>
        {
            readerArea = null;
            Relayout();
            // Done reading: back to the search it was opened from, if the
            // keyboard is still the line's; otherwise the keyboard goes back.
            if (searchOpen && keyboardHeld) StartTyping();
            else ReleaseKeyboard(giveBack: true);
            CatchEscape();
        };
        // Clicked into again after another app had the keyboard.
        Reader.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => HoldReader()), handledEventsToo: true);
        TextHistory.Changed += SyncNotes;

        poll = DispatcherQueue.CreateTimer();
        poll.Interval = PollInterval;
        poll.Tick += (_, _) => Poll();

        regionSettle = DispatcherQueue.CreateTimer();
        regionSettle.Interval = TimeSpan.FromMilliseconds(450);
        regionSettle.IsRepeating = false;
        regionSettle.Tick += (_, _) => UpdateRegion();

        // Out of sight, the line needs next to none of its memory: what drew
        // it goes back to Windows until it comes down again.
        trim = DispatcherQueue.CreateTimer();
        trim.Interval = TrimDelay;
        trim.IsRepeating = false;
        trim.Tick += (_, _) => { if (!shown) Native.TrimWorkingSet(); };

        expire = DispatcherQueue.CreateTimer();
        expire.Interval = ExpireInterval;
        expire.Tick += (_, _) => Expire();

        shell = new Shell(hwnd);
        shell.ToggleRequested += Toggle;
        // Esc puts back the note being read, wherever the focus is.
        shell.EscapePressed += () =>
        {
            if (Reader.IsOpen && !typing) Reader.Close();
            CatchEscape();
        };
        shell.OpenFolderRequested += () =>
            Process.Start(new ProcessStartInfo(Settings.Current.EffectiveScreenshotFolder) { UseShellExecute = true });
        shell.SettingsRequested += OpenSettings;
        shell.QuitRequested += Quit;
        shell.ClipboardChanged += clipboard.OnClipboardChanged;
        shell.ClipboardChanged += clipboardText.OnClipboardChanged;

        Theming.Follow(this, Root);
        Settings.Changed += OnSettingsChanged;
    }

    public void Start()
    {
        // XAML only starts rendering once the window has been activated. Do it
        // with the strip tucked away, then hide the window until it is needed.
        PlaceOn(Native.MonitorUnderCursor());
        Activate();
        AppWindow.Hide();

        shell.Install();
        Expire();
        expire.Start();
        Watch(Settings.Current.RecentAtLaunch);
        // The notes made now, while nothing shows, rather than on first
        // turning to them.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => SyncNotes(fresh: null));
        poll.Start();
        // Once the recent captures have been hung.
        trim.Start();
    }

    public void OpenSettings() => SettingsWindow.Show(shell);

    // MARK: Following the settings

    private void OnSettingsChanged(string name)
    {
        switch (name)
        {
            case nameof(Settings.MaxVisible):
                Relayout();
                break;
            case nameof(Settings.ReduceMotion):
                Wake();
                break;
            case nameof(Settings.MaxHanging):
                TrimCards();
                break;
            case nameof(Settings.ClipboardScreenshotDays):
            case nameof(Settings.TextHistoryDays):
                Expire();
                break;
            case nameof(Settings.ScreenshotFolder):
            case nameof(Settings.RecordingFolder):
            case nameof(Settings.HangRecordings):
            case nameof(Settings.HangClipboardScreenshots):
            case nameof(Settings.HangGameBarCaptures):
                Watch(recent: 0);
                break;
            case nameof(Settings.KeepClipboardText):
                if (!Settings.Current.KeepClipboardText)
                {
                    ShowSide(Side.Photos);
                    DismissNotes();
                }
                else SyncNotes(fresh: null);
                Relayout();
                break;
            case nameof(Settings.MaxTextHistory):
                TextHistory.Trim();
                break;
        }
    }

    /// Watches the screenshot folder, and whichever of the recording, Game
    /// Bar and clipboard folders are wanted. Called again when any of that
    /// changes: photos from a folder no longer watched come down, since
    /// nothing would tell the line if they went.
    private void Watch(int recent)
    {
        foreach (var old in watchers) old.Dispose();
        watchers.Clear();

        var settings = Settings.Current;
        var folders = new List<(string Folder, CaptureKind Kind)> { (settings.EffectiveScreenshotFolder, CaptureKind.Screenshot) };
        if (settings.HangRecordings) folders.Add((settings.EffectiveRecordingFolder, CaptureKind.Recording));
        if (settings.HangGameBarCaptures) folders.Add((settings.GameBarFolder, CaptureKind.Both));
        if (settings.HangClipboardScreenshots) folders.Add((Settings.ClipboardFolder, CaptureKind.Screenshot));
        // The same folder twice, say Game Bar's chosen as the screenshot
        // folder, would hang everything twice over.
        folders = folders.DistinctBy(f => Path.TrimEndingDirectorySeparator(f.Folder), StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var card in cards.ToList())
        {
            bool stillWatched = folders.Any(f =>
                string.Equals(Path.GetDirectoryName(card.FilePath), Path.TrimEndingDirectorySeparator(f.Folder), StringComparison.OrdinalIgnoreCase)
                && Captures.Matches(card.FilePath, f.Kind));
            if (!stillWatched) TakeDown(card);
        }

        foreach (var (folder, kind) in folders)
        {
            var watcher = new CaptureWatcher(folder, kind, DispatcherQueue);
            watcher.Added += (path, fresh) => _ = Hang(path, fresh);
            watcher.Removed += path =>
            {
                if (cards.Find(c => c.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } card) return;
                // Gone from where it was pinned: nothing is left to pin.
                PinnedCaptures.Set(path, false);
                TakeDown(card);
            };
            watchers.Add(watcher);
            watcher.Start(recent, PinnedCaptures.Paths);
        }
    }

    // MARK: Showing and hiding

    private void Poll()
    {
        clipboard.NoticeForeground();
        var now = DateTime.UtcNow;
        var pointer = Native.CursorPosition();
        // Resting in still air, the line wakes when the next draft arrives.
        if (shown && !running && rope.WindStirring) Wake();
        if (shown) KeepOnTop(force: false);

        if (!shown || hiding)
        {
            var settings = Settings.Current;
            if (settings.RevealAtEdge && AtTopEdge(pointer) && drags == 0 && !Native.IsLeftButtonDown()
                && !(settings.QuietInFullScreen && Native.IsSomethingFullScreen()))
            {
                dwellingSince ??= now;
                if (now - dwellingSince >= TimeSpan.FromMilliseconds(settings.RevealDelay))
                    Reveal(Native.MonitorAt(pointer), summoned: false);
            }
            else
            {
                dwellingSince = null;
            }
            return;
        }

        // Dragging a photo out always leaves the line, and must not end the drag.
        // Nor does typing a search end because the pointer wandered off.
        if (drags > 0 || typing) { outsideSince = null; return; }

        if (IsOnLine(pointer))
        {
            pointerVisited = true;
            outsideSince = null;
            return;
        }
        if (summoned && !pointerVisited)
        {
            if (hideAt is { } at && now >= at) TuckAway();
            return;
        }
        outsideSince ??= now;
        if (now - outsideSince >= LeaveGrace) TuckAway();
    }

    private bool AtTopEdge(Native.POINT p)
    {
        var m = Native.MonitorAt(p);
        double corner = DeadCorner * m.Scale;
        return p.Y <= m.Bounds.Top
            && p.X >= m.Bounds.Left + corner
            && p.X < m.Bounds.Right - corner;
    }

    private bool IsOnLine(Native.POINT p)
    {
        if (!monitor.Bounds.Contains(p)) return false;
        double x = (p.X - monitor.Bounds.Left) / monitor.Scale;
        double y = (p.Y - monitor.Bounds.Top) / monitor.Scale;
        var at = new Point(x, y);
        if (pagedFrom is { } before)
        {
            if (before.Contains(at)) return true;
            pagedFrom = null;
            UpdateRegion();
        }
        // Anywhere from the top edge down to the rope keeps it, so the pointer
        // can travel from the edge to the photos.
        return y <= ropeLowest + 12 || Inflate(photoArea, SwingRoom * 0.75).Contains(at);
    }

    private void Reveal(Monitor on, bool summoned, TimeSpan? stay = null)
    {
        if (shown && !hiding)
        {
            // Already down. A new screenshot only keeps it there a bit longer.
            if (this.summoned && !pointerVisited && stay is { } more) hideAt = DateTime.UtcNow + more;
            return;
        }

        // Whatever changed while it was out of sight has long come to rest.
        if (!shown) rope.SettleNow();
        // "3 分钟前" may be an hour ago by now.
        foreach (var note in notes) note.Refresh();
        if (!shown || on.Handle != monitor.Handle) PlaceOn(on);
        AppWindow.Show(activateWindow: false);
        KeepOnTop(force: true);

        shown = true;
        hiding = false;
        this.summoned = summoned;
        pointerVisited = false;
        hideAt = stay is { } s ? DateTime.UtcNow + s : null;
        outsideSince = null;
        dwellingSince = null;

        // From wherever it is, so coming back mid-tuck is seamless. The
        // rope and photos feel it stop.
        rope.MoveStrip(0, lowering: true);
        Wake();
    }

    /// Puts the line above every window, other always-on-top ones too.
    /// Windows sometimes takes always-on-top away from it while the window
    /// still believes it has it, and the line then comes down behind the
    /// window in front; so it is checked while shown, and put back.
    private void KeepOnTop(bool force)
    {
        if (!force && Native.IsTopmost(hwnd)) return;
        // Told again from scratch, which Windows takes when a plain
        // SetWindowPos alone is ignored.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsAlwaysOnTop = true;
        }
        Native.BringToTop(hwnd);
    }

    private void TuckAway()
    {
        if (!shown || hiding) return;
        hiding = true;
        // Aimed a little past the top, so it leaves briskly; the window
        // hides as it gets there.
        rope.MoveStrip(-StripHeight - 16, lowering: false);
        Wake();
    }

    private void FinishTucking()
    {
        hiding = false;
        pagedFrom = null;
        shown = false;
        Reader.Close(now: true);
        CloseSearch();
        // Tucked away, the pointer may have left a card without saying so.
        foreach (var peg in pegs.Values) peg.Steady = false;
        rope.PlaceStrip(-StripHeight);
        Strip.Translation = new Vector3(0, (float)-StripHeight, 0);
        AppWindow.Hide();
        trim.Start();
    }

    private void Toggle()
    {
        if (shown && !hiding) TuckAway();
        else Reveal(Native.MonitorUnderCursor(), summoned: true);
    }

    /// Spans the top of the given screen. The content scales with the
    /// screen's DPI on its own; the size is in physical pixels.
    private void PlaceOn(Monitor m)
    {
        monitor = m;
        int height = (int)Math.Ceiling(StripHeight * m.Scale);
        AppWindow.MoveAndResize(new RectInt32(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, height));
    }

    // MARK: The photos

    private async Task Hang(string path, bool fresh)
    {
        if (cards.Exists(c => c.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        var card = new PhotoCard(path) { Pinned = PinnedCaptures.Contains(path) };
        if (!await card.LoadAsync()) return;
        if (cards.Exists(c => c.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

        var peg = new Rope.Peg(card.CardHeight, Math.Clamp(card.CardHeight / AverageCardHeight, 0.7, 1.4));
        card.LetGo += () =>
        {
            PinnedCaptures.Set(card.FilePath, false);
            TakeDown(card, discard: true);
        };
        card.PinToggled += () => TogglePin(card);
        card.DraggingChanged += dragging => drags = Math.Max(0, drags + (dragging ? 1 : -1));
        card.Pressed += down => { peg.ScaleTarget = down ? 0.965 : 1; Wake(); };
        HoldStill(card, peg);
        card.Poked += at => { rope.Poke(peg, at); Wake(); };
        card.Opacity = 0;
        rope.Pegs.Add(peg);
        pegs[card] = peg;
        // A new capture is shown even if the line was showing copied text.
        if (fresh) ShowSide(Side.Photos, dropIn: false);

        // Pinned ones first, then newest on the left: the rest slide along to
        // make room. A new capture is always the newest, and the line scrolls
        // back to show it.
        int index = Place(card);
        Photos.Children.Add(card);
        if (fresh) ScrollToShow(index);
        TrimCards();
        Relayout();

        // A new capture falls onto the line. One that was already there
        // just hangs, the rope sagging under it from the start.
        if (fresh) rope.DropIn(peg);
        else rope.HangNow(peg);
        if (!shown) rope.SettleNow();
        ShowFrame();
        Wake();
        if (!shown) trim.Start();

        if (fresh && Settings.Current.RevealOnCapture)
            Reveal(Native.MonitorUnderCursor(), summoned: true, stay: FreshShotStay);
    }

    /// Puts the card among the others where it belongs: pinned ones first,
    /// then the newest. Returns where that is.
    private int Place(PhotoCard card)
    {
        static bool Precedes(PhotoCard a, PhotoCard b) => a.Pinned != b.Pinned ? a.Pinned : a.TakenAt >= b.TakenAt;
        int index = cards.FindIndex(c => Precedes(card, c));
        if (index < 0) index = cards.Count;
        cards.Insert(index, card);
        return index;
    }

    /// Pinned, it moves to the front of the line, and the line pages there to
    /// show it; unpinned, back among the rest. There is a limit to how many.
    private void TogglePin(PhotoCard card)
    {
        bool pinned = !card.Pinned;
        if (!PinnedCaptures.Set(card.FilePath, pinned))
        {
            card.Flash($"最多置顶 {Settings.Current.MaxPinnedCaptures} 张");
            return;
        }
        card.Pinned = pinned;
        card.Flash(pinned ? "已置顶" : "已取消置顶");
        cards.Remove(card);
        int index = Place(card);
        if (pinned) ScrollToShow(index);
        KeepPointerOnLine();
        Relayout();
    }

    /// Takes the oldest down while there are more than the settings keep.
    /// Pinned ones stay.
    private void TrimCards()
    {
        while (cards.Count > Settings.Current.MaxHanging && cards.FindLastIndex(c => !c.Pinned) is var oldest and >= 0)
            TakeDown(cards[oldest], discard: true);
    }

    /// Clears away what has been kept longer than the settings say: copied
    /// text, and screenshots kept from the clipboard. Pinned ones stay.
    private static void Expire()
    {
        TextHistory.Expire();
        _ = Task.Run(ClipboardCapture.ExpireOld);
    }

    /// Off the line. The file itself stays where it is, unless it is a
    /// screenshot Clipline kept from the clipboard and it is being let go:
    /// nobody else knows that file, so it goes to the Recycle Bin rather
    /// than piling up unseen.
    private bool TakeDown(PhotoCard card, bool discard = false)
    {
        int index = cards.IndexOf(card);
        if (index < 0) return false;
        cards.RemoveAt(index);
        // Whatever was in view stays in view.
        if (index < first) first--;
        // It drops away, and the rope, relieved of its weight, springs up.
        // The card goes once it is out of sight.
        card.IsHitTestVisible = false;
        if (pegs.TryGetValue(card, out var peg)) rope.Release(peg);
        Relayout();
        if (discard && string.Equals(Path.GetDirectoryName(card.FilePath), Settings.ClipboardFolder, StringComparison.OrdinalIgnoreCase))
        {
            string path = card.FilePath;
            _ = Task.Run(() => Native.Recycle(path));
        }
        return true;
    }

    // MARK: Scrolling

    /// The wheel moves along the line one photo per notch: down or right for
    /// older ones, up or left for newer ones. Touchpads send many small steps,
    /// which add up to a notch.
    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Root).Properties;
        int delta = properties.MouseWheelDelta;
        wheel += properties.IsHorizontalMouseWheel ? delta : -delta;
        int notches = wheel / WheelNotch;
        if (notches != 0)
        {
            wheel -= notches * WheelNotch;
            ScrollBy(notches);
        }
        e.Handled = true;
    }

    private void ScrollBy(int photos)
    {
        int last = Math.Max(0, Hanging.Count - capacity);
        int target = Math.Clamp(first + photos, 0, last);
        if (target == first) return;
        first = target;
        Reader.Close();
        KeepPointerOnLine();
        Relayout();
    }

    /// Pages the line, if need be, so the one at `index` is in view.
    private void ScrollToShow(int index)
    {
        if (index < 0) return;
        if (index < first) first = index;
        else if (index >= first + capacity) first = index - capacity + 1;
    }

    /// What is in view is about to change under the pointer that changed it:
    /// where things were stays part of the line until the pointer leaves.
    private void KeepPointerOnLine() =>
        pagedFrom = pagedFrom is { } earlier ? Union(earlier, Inflate(photoArea, SwingRoom)) : Inflate(photoArea, SwingRoom);

    // MARK: Captures or copied text

    /// What hangs on the line now, newest first.
    private IReadOnlyList<UserControl> Hanging => side == Side.Photos ? cards : shownNotes;

    private void PrepareSideSwitch()
    {
        ModeThumb.Width = SideTabWidth;
        ModeThumb.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
        foreach (var (tab, content, to) in new[] { (PhotosTab, PhotosTabContent, Side.Photos), (NotesTab, NotesTabContent, Side.Notes) })
        {
            tab.Width = SideTabWidth;
            content.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
            tab.PointerEntered += (_, _) => { if (side != to) content.Opacity = 0.85; };
            tab.PointerExited += (_, _) => ShowSideSwitch();
            tab.Tapped += (_, e) => { e.Handled = true; ShowSide(to); };
            tab.DoubleTapped += (_, e) => e.Handled = true;
        }
    }

    /// Which side is showing, and how much each holds.
    private void ShowSideSwitch()
    {
        static void Count(TextBlock label, int count)
        {
            label.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            label.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Count(PhotosCount, cards.Count);
        Count(NotesCount, TextHistory.Items.Count);
        ModeThumb.Translation = new Vector3(side == Side.Photos ? 0 : (float)SideTabWidth, 0, 0);
        PhotosTabContent.Opacity = side == Side.Photos ? 1 : 0.55;
        NotesTabContent.Opacity = side == Side.Notes ? 1 : 0.55;
    }

    /// Turns the line to the other side. The side leaving fades off the rope
    /// where it hangs; the one arriving drops onto it, one after another
    /// from the left, unless `dropIn` is false and it just fades in.
    private void ShowSide(Side to, bool dropIn = true)
    {
        if (to == side) return;
        Reader.Close();
        // The search is for the copied text, and only shows on that side.
        if (to == Side.Photos) CloseSearch();
        SearchArea.Visibility = to == Side.Notes ? Visibility.Visible : Visibility.Collapsed;
        KeepPointerOnLine();
        (first, otherFirst) = (otherFirst, first);
        side = to;
        if (to == Side.Notes) SyncNotes(fresh: null);
        else Relayout();

        if (!shown)
        {
            rope.SettleNow();
            ShowFrame();
            return;
        }
        if (dropIn)
        {
            var showing = Hanging;
            int end = Math.Min(showing.Count, first + capacity);
            for (int i = first; i < end; i++) rope.DropIn(pegs[showing[i]], later: 0.05 * (i - first));
        }
        ShowFrame();
        Wake();
    }

    /// Brings the notes in step with the text history: new ones hung, the
    /// one just copied falling in; deleted ones dropping away; the rest in
    /// the history's order, sliding there if it changed. Kept up on either
    /// side, weightless and out of sight on the photos' side, so turning to
    /// the text never has to make fifty notes at once.
    private void SyncNotes(TextClip? fresh)
    {
        if (!Settings.Current.KeepClipboardText)
        {
            ShowSideSwitch();
            return;
        }
        bool onNotes = side == Side.Notes;
        var history = TextHistory.Items;
        var kept = history.ToHashSet();
        foreach (var gone in notes.Where(n => !kept.Contains(n.Clipping)).ToList()) TakeDown(gone);

        var existing = notes.ToDictionary(n => n.Clipping);
        var added = new List<NoteCard>();
        notes.Clear();
        foreach (var clip in history)
        {
            if (existing.TryGetValue(clip, out var note)) note.Refresh();
            else added.Add(note = NewNote(clip));
            notes.Add(note);
        }
        FilterNotes();
        // The one just copied comes to the front of the notes, wherever the
        // notes' page was; on the photos' side that page is kept aside.
        if (fresh is not null)
        {
            if (onNotes) ScrollToShow(shownNotes.FindIndex(n => n.Clipping == fresh));
            else otherFirst = 0;
        }
        Relayout();

        foreach (var note in added)
        {
            if (note.Clipping == fresh && shown && onNotes) rope.DropIn(pegs[note]);
            else rope.HangNow(pegs[note]);
        }
        if (!shown) rope.SettleNow();
        ShowFrame();
        Wake();
        if (!shown && added.Count > 0) trim.Start();
    }

    private NoteCard NewNote(TextClip clip)
    {
        var note = new NoteCard(clip) { Opacity = 0 };
        // In the tree first, so it can be measured.
        Photos.Children.Add(note);
        note.MeasureHeight();
        var peg = new Rope.Peg(note.CardHeight, Math.Clamp(note.CardHeight / AverageCardHeight, 0.7, 1.4) * NoteCard.PaperWeight);
        note.LetGo += () => TextHistory.Remove(clip);
        note.ReadRequested += () => Read(note);
        note.DraggingChanged += dragging => drags = Math.Max(0, drags + (dragging ? 1 : -1));
        note.Pressed += down => { peg.ScaleTarget = down ? 0.965 : 1; Wake(); };
        HoldStill(note, peg);
        note.Poked += at => { rope.Poke(peg, at); Wake(); };
        note.Resized += () => { peg.Reshape(note.CardHeight); Relayout(); };
        note.PinChanged += () =>
        {
            // Pinned, it went to the front: page there to show it.
            if (!clip.Pinned) return;
            ScrollToShow(shownNotes.IndexOf(note));
            KeepPointerOnLine();
            Relayout();
        };
        rope.Pegs.Add(peg);
        pegs[note] = peg;
        return note;
    }

    /// A card stops swinging while the pointer is over it, to be looked at.
    private void HoldStill(UIElement card, Rope.Peg peg)
    {
        void Steady(bool on)
        {
            peg.Steady = on;
            Wake();
        }
        card.PointerEntered += (_, _) => Steady(true);
        card.PointerExited += (_, _) => Steady(false);
        card.PointerCanceled += (_, _) => Steady(false);
    }

    /// Off the line: deleted from the history, it drops away.
    private void TakeDown(NoteCard note)
    {
        int index = notes.IndexOf(note);
        if (index < 0) return;
        notes.RemoveAt(index);
        int shownAt = shownNotes.IndexOf(note);
        if (shownAt >= 0)
        {
            shownNotes.RemoveAt(shownAt);
            if (shownAt < first) first--;
        }
        note.IsHitTestVisible = false;
        if (pegs.TryGetValue(note, out var peg)) rope.Release(peg);
    }

    /// Lets go of every note, once copied text is no longer kept.
    private void DismissNotes()
    {
        foreach (var note in notes)
        {
            if (pegs.Remove(note, out var peg)) rope.Pegs.Remove(peg);
            lastDrawn.Remove(note);
            Photos.Children.Remove(note);
        }
        notes.Clear();
        shownNotes.Clear();
    }

    /// Holds a note up to read it in full, in front of the line over where
    /// it hangs.
    private void Read(NoteCard note)
    {
        if (!pegs.TryGetValue(note, out var peg)) return;
        double width = NoteReader.SheetWidth;
        var size = Reader.Open(note.Clipping, StripHeight - ReaderTop - 14);
        double x = Math.Clamp(peg.TargetX - width / 2, 16, Math.Max(16, Root.ActualWidth - width - 16));
        Canvas.SetLeft(Reader, x);
        Canvas.SetTop(Reader, ReaderTop);
        Reader.GrowFrom(peg.TargetX - x);
        readerArea = new Rect(x, ReaderTop, size.Width, size.Height);
        Relayout();
        HoldReader();
    }

    /// Takes the keyboard so Esc can put the note back, from the search box
    /// too: the note is in front, so Esc is for it first.
    private void HoldReader()
    {
        typing = false;
        HoldKeyboard();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusReader);
        CatchEscape();
    }

    /// The note being read takes the focus, for Esc, unless something in it
    /// already has it: the text pressed to select some of it loses its
    /// highlight with the focus, and nobody can see what they picked.
    private void FocusReader()
    {
        if (!Reader.IsOpen || typing) return;
        for (var at = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; at is not null; at = VisualTreeHelper.GetParent(at))
            if (at == Reader) return;
        Reader.Focus(FocusState.Programmatic);
    }

    // MARK: Searching the copied text

    /// The magnifier under the line opens a box to type into. Only notes
    /// whose text, or the app they came from, has every word typed stay on
    /// the rope. Enter copies the first of them; Esc empties the box, and
    /// then closes it.
    private void PrepareSearch()
    {
        SearchButton.Tapped += (_, e) =>
        {
            e.Handled = true;
            if (searchOpen) CloseSearch();
            else OpenSearch();
        };
        SearchButton.DoubleTapped += (_, e) => e.Handled = true;
        SearchButton.PointerEntered += (_, _) => SearchGlyph.Opacity = 1;
        SearchButton.PointerExited += (_, _) => SearchGlyph.Opacity = searchOpen ? 1 : 0.85;
        SearchBox.TextChanged += (_, _) => Search(SearchBox.Text);
        SearchBox.KeyDown += OnSearchKey;
        // Clicked into again after typing in another app for a while.
        SearchBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => StartTyping()), handledEventsToo: true);
        SearchBox.Tapped += (_, e) => e.Handled = true;
        SearchBox.DoubleTapped += (_, e) => e.Handled = true;
        // Clicked away into another app: the keyboard is that app's again,
        // and the line goes back to never taking it.
        Activated += (_, e) =>
        {
            active = e.WindowActivationState != WindowActivationState.Deactivated;
            if (active)
            {
                // Activating puts the focus back where it last was, which may
                // be nowhere: the note being read wants it, for Esc.
                if (Reader.IsOpen && !typing)
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusReader);
            }
            else
            {
                typing = false;
                ReleaseKeyboard(giveBack: false);
            }
            CatchEscape();
        };
    }

    private const double SearchWidth = 190;
    private static readonly TimeSpan SearchGrowTime = TimeSpan.FromMilliseconds(240);
    private bool searchOpen;
    private EventHandler<object>? searchGrowing;

    private void OpenSearch()
    {
        double from = SearchBox.Visibility == Visibility.Visible ? SearchBox.Width : 0;
        searchOpen = true;
        SearchBox.Visibility = Visibility.Visible;
        SearchGlyph.Opacity = 1;
        // Laid out at its full width first, so the window takes clicks over
        // all of it from the start; then it grows into it.
        SearchBox.Width = SearchWidth;
        Relayout();
        GrowSearch(from, SearchWidth, done: null);
        StartTyping();
    }

    /// Empties the box and puts it away, and every note back on the rope.
    private void CloseSearch()
    {
        StopTyping(giveBack: true);
        if (!searchOpen && query.Length == 0) return;
        searchOpen = false;
        SearchGlyph.Opacity = 0.85;
        SearchBox.Text = "";
        Search("");
        GrowSearch(SearchBox.Width, 0, done: () =>
        {
            SearchBox.Visibility = Visibility.Collapsed;
            Relayout();
        });
    }

    /// Widens or narrows the box frame by frame, fading it with its width,
    /// and keeps the switch it sits in centred as it does.
    private void GrowSearch(double from, double to, Action? done)
    {
        if (searchGrowing is not null) CompositionTarget.Rendering -= searchGrowing;
        if (double.IsNaN(from)) from = 0;
        var duration = SearchGrowTime * (Math.Abs(to - from) / SearchWidth);
        var clock = Stopwatch.StartNew();
        EventHandler<object>? step = null;
        step = (_, _) =>
        {
            double t = duration > TimeSpan.Zero ? Math.Min(1, clock.Elapsed / duration) : 1;
            double eased = 1 - Math.Pow(1 - t, 3);
            double width = from + (to - from) * eased;
            SearchBox.Width = width;
            SearchBox.Opacity = width / SearchWidth;
            CenterSideSwitch();
            if (t < 1) return;
            CompositionTarget.Rendering -= step;
            searchGrowing = null;
            done?.Invoke();
        };
        searchGrowing = step;
        CompositionTarget.Rendering += step;
    }

    private void CenterSideSwitch()
    {
        if (ModeSwitch.Visibility != Visibility.Visible || Root.ActualWidth <= 0) return;
        ModeSwitch.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(ModeSwitch, Math.Round((Root.ActualWidth - ModeSwitch.DesiredSize.Width) / 2));
    }

    /// The overlay never takes the keyboard from the app in front, except to
    /// be typed into: then it does, until the search is done.
    private void StartTyping()
    {
        typing = true;
        HoldKeyboard();
        // The search box has Esc of its own.
        CatchEscape();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (typing) SearchBox.Focus(FocusState.Programmatic);
        });
    }

    /// Lets go of the keyboard, handing it back to the app that had it if asked.
    private void StopTyping(bool giveBack)
    {
        if (!typing) return;
        typing = false;
        // A note still held up to read keeps it, for Esc.
        if (Reader.IsOpen) HoldReader();
        else ReleaseKeyboard(giveBack);
        CatchEscape();
    }

    /// The window is the active one, and has the keyboard.
    private bool active;

    /// Esc is the line's while a note is held up to read and the line is the
    /// active window, unless the search box is being typed into.
    private void CatchEscape() => shell?.CatchEscape(active && Reader.IsOpen && !typing);

    private void HoldKeyboard()
    {
        if (keyboardHeld) return;
        keyboardHeld = true;
        var before = Native.GetForegroundWindow();
        if (before != hwnd) typedOver = before;
        Native.SetActivatable(hwnd, true);
    }

    private void ReleaseKeyboard(bool giveBack)
    {
        if (!keyboardHeld) return;
        keyboardHeld = false;
        Native.SetActivatable(hwnd, false);
        if (giveBack && typedOver != IntPtr.Zero && Native.IsWindow(typedOver)) Native.SetForegroundWindow(typedOver);
        typedOver = IntPtr.Zero;
    }

    private void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Text = "";
                    Search("");
                }
                else CloseSearch();
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                // Copied, and back to the app it is wanted in.
                if (shownNotes.Count == 0) break;
                shownNotes[0].Copy();
                StopTyping(giveBack: true);
                break;
        }
    }

    private void Search(string text)
    {
        string trimmed = text.Trim();
        if (trimmed == query) return;
        query = trimmed;
        Reader.Close();
        FilterNotes();
        first = 0;
        Relayout();
    }

    /// Picks out the notes the search finds, and says how many there are.
    private void FilterNotes()
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        shownNotes = words.Length == 0 ? notes.ToList() : notes.Where(n => Matches(n.Clipping, words)).ToList();
        SearchCount.Text = $"{shownNotes.Count} 条";
        SearchCount.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(TextClip clip, string[] words) => words.All(word =>
        clip.Text.Contains(word, StringComparison.CurrentCultureIgnoreCase)
        || clip.Source?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true);

    // MARK: Layout

    /// Decides where everything should end up. Getting there is up to the
    /// rope, a frame at a time.
    private void Relayout()
    {
        double width = Root.ActualWidth;
        if (width <= 0) return;
        rope.Width = width;

        // As many as fit between the ends, up to the limit.
        var showing = Hanging;
        int fits = (int)((width - 2 * EndRoom + CardGap) / CardStep);
        capacity = Math.Clamp(fits, 1, Settings.Current.MaxVisible);
        first = Math.Clamp(first, 0, Math.Max(0, showing.Count - capacity));
        int inView = Math.Min(capacity, showing.Count);

        // The photos in view are centred. The others hang on along the rope
        // beyond them, out of sight and weightless, ready to slide in.
        double start = (width - (inView * CardStep - CardGap)) / 2;
        for (int i = 0; i < showing.Count; i++)
        {
            var card = showing[i];
            var peg = pegs[card];
            double x = start + (i - first) * CardStep + PhotoCard.CardWidth / 2;
            // Moving along, the peg goes first and the photo swings after it.
            if (double.IsNaN(peg.X)) peg.X = x;
            peg.TargetX = x;

            bool visible = i >= first && i < first + inView;
            peg.TargetPresence = visible ? 1 : 0;
            ((ILineCard)card).SetInView(visible);
        }
        // The other side, and the notes a search leaves out, fade off the
        // rope where they hang.
        var hanging = new HashSet<UserControl>(showing);
        foreach (var card in cards.Concat<UserControl>(notes))
        {
            if (hanging.Contains(card)) continue;
            pegs[card].TargetPresence = 0;
            ((ILineCard)card).SetInView(false);
        }

        // Where the photos in view will rest, on the rope as it will sag.
        Rect? area = null;
        for (int i = first; i < first + inView; i++)
        {
            var peg = pegs[showing[i]];
            double top = rope.RestingY(peg.TargetX) - PhotoCard.RopeGrip;
            area = Union(area, new Rect(peg.TargetX - PhotoCard.CardWidth / 2, top, PhotoCard.CardWidth, ((ILineCard)showing[i]).CardHeight));
        }

        EmptyHint.Visibility = showing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintText.Text = side == Side.Photos ? PhotosHint
            : query.Length > 0 ? $"没有找到包含「{query}」的文字"
            : NotesHint;
        if (area is not { } photos)
        {
            double w = EmptyHint.ActualWidth > 0 ? EmptyHint.ActualWidth : 340;
            double h = EmptyHint.ActualHeight > 0 ? EmptyHint.ActualHeight : 36;
            photos = new Rect((width - w) / 2, HintTop, w, h);
        }
        else
        {
            // How many are hidden at each end, and a way to get to them.
            double middle = photos.Top + photos.Height / 2;
            photos = PlaceChip(NewerChip, NewerCount, first, photos, middle, left: true) ?? photos;
            photos = PlaceChip(OlderChip, OlderCount, showing.Count - first - inView, photos, middle, left: false) ?? photos;
        }
        if (showing.Count == 0)
        {
            NewerChip.Visibility = Visibility.Collapsed;
            OlderChip.Visibility = Visibility.Collapsed;
        }
        photos = PlaceSideSwitch(photos, width);
        if (readerArea is { } reading) photos = Union(photos, reading);
        photoArea = photos;
        ropeLowest = rope.Lowest();

        // New positions take effect at once, but the photos glide there. Cut
        // the region around both until they arrive, then around the new ones.
        var target = Inflate(photoArea, SwingRoom);
        if (!wholeWindow) ApplyRegion(Including(previousRegionArea is { } before ? Union(before, target) : target, pagedFrom));
        previousRegionArea = target;
        regionSettle.Stop();
        regionSettle.Start();

        ShowFrame();
        Wake();
    }

    /// Shows `count` beside the photos in view, or hides the chip when there
    /// is nothing that way. Returns the area grown to include it.
    private static Rect? PlaceChip(FrameworkElement chip, TextBlock label, int count, Rect photos, double middle, bool left)
    {
        if (count <= 0)
        {
            chip.Visibility = Visibility.Collapsed;
            return null;
        }
        label.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        chip.Visibility = Visibility.Visible;
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = chip.DesiredSize;
        double x = left ? photos.Left - 20 - size.Width : photos.Right + 20;
        double y = middle - size.Height / 2;
        Canvas.SetLeft(chip, x);
        Canvas.SetTop(chip, y);
        return Union(photos, new Rect(x, y, size.Width, size.Height));
    }

    /// Centres the switch between captures and copied text under `above`,
    /// gliding when what hangs above it grows or shrinks. Returns the area
    /// grown to include it.
    private Rect PlaceSideSwitch(Rect above, double width)
    {
        if (!Settings.Current.KeepClipboardText)
        {
            ModeSwitch.Visibility = Visibility.Collapsed;
            return above;
        }
        ModeSwitch.Visibility = Visibility.Visible;
        ShowSideSwitch();
        ModeSwitch.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ModeSwitch.DesiredSize;
        double x = Math.Round((width - size.Width) / 2), y = Math.Round(above.Bottom + 16);
        Canvas.SetLeft(ModeSwitch, x);
        // Raised a little off the strip, for its shadow.
        ModeSwitch.Translation = new Vector3(0, (float)y, 16);
        ModeSwitch.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(320) };
        return Union(above, new Rect(x, y, size.Width, size.Height));
    }

    // MARK: Motion

    /// Keeps the simulation running while there is anything to see move.
    private void Wake()
    {
        if (running || !(shown || hiding)) return;
        running = true;
        lastFrame = null;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Sleep()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        // Stepped by the time each frame will be shown at, which is steadier
        // than when this happens to run.
        var at = ((RenderingEventArgs)e).RenderingTime;
        if (lastFrame is { } last && at <= last) return;
        double elapsed = lastFrame is { } previous ? (at - previous).TotalSeconds : 1.0 / 60;
        lastFrame = at;

        rope.Step(elapsed);
        foreach (var (card, _) in pegs.Where(p => p.Value.Removed).ToList())
        {
            pegs.Remove(card);
            lastDrawn.Remove(card);
            Photos.Children.Remove(card);
        }
        ShowFrame();

        if (hiding && rope.StripY <= -StripHeight + 1) FinishTucking();
        if (wholeWindow != rope.InFlight || wasUnsettled != rope.Unsettled) UpdateRegion();
        if (!shown && !hiding || rope.Settled) Sleep();
    }

    /// Puts the strip, the rope and every photo where the simulation says.
    /// Only what has visibly changed is touched: each change costs a frame
    /// of composition work, shadows and all.
    private void ShowFrame()
    {
        float stripY = (float)rope.StripY;
        if (Strip.Translation.Y != stripY) Strip.Translation = new Vector3(0, stripY, 0);
        if (rope.TakeTraceIfMoved() is { } trace)
        {
            ropeLine.Points = trace;
            // A collection of points can only belong to one line.
            haloLine.Points = Copy(trace);
            shineLine.Points = Copy(trace);
        }
        foreach (var (card, peg) in pegs)
        {
            if (double.IsNaN(peg.X)) continue;
            var drawn = new Drawn(
                // Finer than this nobody can see, and every change is a frame to draw.
                Quantize(peg.PivotX - PhotoCard.CardWidth / 2, 0.1),
                Quantize(peg.PivotY - PhotoCard.RopeGrip, 0.1),
                Quantize(peg.Angle * 180 / Math.PI, 0.05),
                Quantize(peg.Scale, 0.001),
                Quantize(peg.ShownOpacity, 0.01));
            // Out of sight, nothing about it shows.
            if (drawn.Opacity == 0) drawn = drawn with { X = 0, Y = 0, Angle = 0, Scale = 1 };
            if (lastDrawn.TryGetValue(card, out var last) && last == drawn) continue;
            lastDrawn[card] = drawn;
            card.Translation = new Vector3(drawn.X, drawn.Y, 0);
            card.Rotation = drawn.Angle;
            card.Scale = new Vector3(drawn.Scale, drawn.Scale, 1);
            card.Opacity = drawn.Opacity;
        }
    }

    private readonly record struct Drawn(float X, float Y, float Angle, float Scale, float Opacity);
    private readonly Dictionary<UserControl, Drawn> lastDrawn = new();

    private static float Quantize(double value, double step) => (float)(Math.Round(value / step) * step);

    private static PointCollection Copy(PointCollection points)
    {
        var copy = new PointCollection();
        foreach (var point in points) copy.Add(point);
        return copy;
    }

    private static PathGeometry LineGeometry(PolyLineSegment line)
    {
        var figure = new PathFigure { StartPoint = new Point(0, Rope.Top), IsClosed = false, IsFilled = false };
        figure.Segments.Add(line);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// The pointer brushes photos and plucks the rope as it moves over them.
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Strip);
        var p = point.Position;
        long now = Stopwatch.GetTimestamp();
        if (lastPointer is { } before)
        {
            double dt = (double)(now - lastPointerTicks) / Stopwatch.Frequency;
            if (dt > 0 && dt < 0.1)
            {
                // Smoothed a little: pointer events come unevenly.
                var raw = new Point((p.X - before.X) / dt, (p.Y - before.Y) / dt);
                double keep = Math.Exp(-dt / 0.02);
                pointerVelocity = new Point(keep * pointerVelocity.X + (1 - keep) * raw.X,
                                            keep * pointerVelocity.Y + (1 - keep) * raw.Y);
                // Not through the sheet of a note being read.
                if (!point.Properties.IsLeftButtonPressed && drags == 0 && !Reader.IsOpen)
                {
                    rope.Brush(p, pointerVelocity, dt);
                    Wake();
                }
            }
            else pointerVelocity = default;
        }
        lastPointer = p;
        lastPointerTicks = now;
    }

    /// Cuts the window down to the rope and the photos. While a photo falls
    /// in or away it may be anywhere, so the whole window stays.
    private void UpdateRegion()
    {
        wholeWindow = rope.InFlight;
        wasUnsettled = rope.Unsettled;
        ropeLowest = rope.Lowest();
        if (wholeWindow)
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? monitor.Scale;
            var all = new Native.RECT { Right = (int)Math.Ceiling(Root.ActualWidth * scale), Bottom = (int)Math.Ceiling(StripHeight * scale) };
            Native.SetRegion(hwnd, new Native.POINT[3], all);
            return;
        }
        // Where it was paged from stays clickable while the pointer is there.
        ApplyRegion(Including(Inflate(photoArea, SwingRoom), pagedFrom));
    }

    private void ApplyRegion(Rect photos)
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? monitor.Scale;
        int Px(double dip) => (int)Math.Round(dip * scale);
        double widthDip = Root.ActualWidth;
        int width = Px(widthDip);

        // A band that follows the rope: across the rest of the screen, clicks
        // just above and below it reach the app underneath. While the rope
        // moves it allows for it bouncing past where it will rest.
        double bounce = RopeReach + (rope.Unsettled ? 0.6 * (ropeLowest - Rope.Top) + 12 : 0);
        const int Steps = 48;
        var band = new Native.POINT[2 * (Steps + 1)];
        for (int i = 0; i <= Steps; i++)
        {
            double x = widthDip * i / Steps;
            double now = rope.Y(x), rest = rope.RestingY(x);
            band[i] = new Native.POINT { X = Px(x), Y = Px(Math.Min(now, rest) - bounce) };
            band[band.Length - 1 - i] = new Native.POINT { X = Px(x), Y = Px(Math.Max(now, rest) + bounce) };
        }

        Native.SetRegion(hwnd, band, new Native.RECT
        {
            Left = Math.Max(0, Px(photos.Left)), Top = Math.Max(0, Px(photos.Top)),
            Right = Math.Min(width, Px(photos.Right)), Bottom = Px(photos.Bottom),
        });
    }

    private static Rect Inflate(Rect r, double by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static Rect Including(Rect a, Rect? b) => b is { } r ? Union(r, a) : a;

    private static Rect Union(Rect? a, Rect b)
    {
        if (a is not { } r) return b;
        double left = Math.Min(r.Left, b.Left), top = Math.Min(r.Top, b.Top);
        return new Rect(left, top, Math.Max(r.Right, b.Right) - left, Math.Max(r.Bottom, b.Bottom) - top);
    }

    // MARK: Quitting

    private void Quit()
    {
        poll.Stop();
        foreach (var watcher in watchers) watcher.Dispose();
        Settings.Changed -= OnSettingsChanged;
        TextHistory.Changed -= SyncNotes;
        TextHistory.SaveNow();
        shell.Dispose();
        Close();
        Application.Current.Exit();
    }
}
