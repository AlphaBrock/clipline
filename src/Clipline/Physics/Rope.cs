using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Clipline;

/// The line and everything on it, simulated as the real thing a frame at a time.
///
/// The rope is a string under tension tied at both ends of the screen: a row
/// of small masses whose up-and-down motion follows the wave equation. A
/// photo landing on it sends a ripple running both ways, and the rope
/// bounces under the weight: quick and light when little hangs on it, slow
/// and heavy when it is laden. At rest it takes exactly the shape of a cable
/// carrying those loads: straight between pegs with a corner at each, and
/// the deeper the more and the heavier the photos.
///
/// Each photo is a rigid card hanging from its peg, a physical pendulum with
/// the inertia of its own size. It is swung by whatever its peg does
/// (sliding along the line, the rope bouncing, the whole line dropping into
/// view), by gusts of wind travelling along the line from one photo to the
/// next, by the pointer brushing past and by neighbours knocking into it.
/// Its swing tugs at the rope in turn. A new photo falls in from above and
/// the rope catches it; one taken down drops away and the rope springs up.
///
/// Distances are in DIPs, in the coordinates of the strip, with y going down.
/// The rope's tension is the unit of force.
public sealed class Rope
{
    /// Where the rope is tied at both ends, below the top of the strip.
    public const double Top = 40;
    /// In DIPs per second squared: what makes a print this size swing about
    /// once a second, as a real print on a real line seems to. Real gravity
    /// at screen scale would make it twitch like a key fob.
    public const double Gravity = 3600;
    /// How far the rope sags under its own weight.
    private const double EmptySag = 8;
    /// How far one photo of average weight pulls the middle of the rope down.
    private const double SagPerPhoto = 7.5;
    /// Between the masses making up the rope.
    private const double NodeSpacing = 20;

    // The rope loses its bounce to the air along its whole length, and to
    // its own fibres rubbing, which stills the fine ripples first.
    private const double RopeDrag = 3.4;
    private const double RopeFriction = 2.5e-4;
    // Air drag on a swinging photo, per second: gentle swings linger, wild
    // ones are reined in quickly.
    private const double SwingDrag = 1.3, SwingDragQuadratic = 0.7;
    // A peg sliding along the rope: brisk, barely overshooting.
    private const double SlideStiffness = 110, SlideDamping = 18;
    /// Coming into view or leaving it along the rope takes a quarter of a second.
    private const double PresenceRate = 4;
    /// The share of a falling photo's speed the rope takes up when it is
    /// caught. The rest goes into the peg's spring and the rope's give.
    private const double CatchTransfer = 0.4;
    // What a photo feels of its peg being moved: the grip's give smooths it
    // over this long, a slide is felt at this share, and neither is ever
    // felt harder than these.
    private const double GripLag = 0.035, SlideFelt = 0.4;
    private const double MaxSideways = 0.3 * Gravity, MaxUpDown = 0.6 * Gravity;
    /// Photos knocking together lose most of their swing to it.
    private const double Restitution = 0.3;
    /// A photo dropping off the line has faded out after this long.
    private const double DropFade = 0.42;
    /// How hard the pointer grips a photo it brushes past, and the rope:
    /// a light touch, not a hand.
    private const double BrushGrip = 0.08, PluckGrip = 0.2;
    /// The pointer is no finger: however fast it flicks past, it pushes as
    /// if moving no faster than this, in DIPs a second.
    private const double BrushSpeed = 450;
    /// The fastest the pointer may set a photo swinging, in radians a second.
    private const double MaxBrushSpin = 1.4;
    /// The fastest a photo may be sent spinning by a push, in radians a second.
    private const double MaxSpin = 5;

    public enum State
    {
        /// On the rope.
        Hanging,
        /// Falling in from above, until the rope catches it.
        Arriving,
        /// Let go of, falling away and fading.
        Leaving,
    }

    /// One photo and its peg.
    public sealed class Peg
    {
        /// Where the peg is along the rope, where it is heading, and how fast.
        public double X = double.NaN, TargetX, Velocity;
        internal double Acceleration;
        /// How the pivot accelerates, as the photo feels it: smoothed, since
        /// a rope of a hundred beads jitters in a way a real one does not.
        internal double FeltX, FeltY;
        /// How heavy the photo is: 1 for one of average height.
        public double Weight = 1;
        /// 1 while it hangs in view, 0 when scrolled out of view.
        public double Presence, TargetPresence;
        public State State;
        /// Taken down: it is falling away.
        public bool Gone => State == State.Leaving;
        /// Out of sight for good: its card can go.
        public bool Removed { get; internal set; }

        /// The swing: clockwise from hanging straight down, in radians.
        public double Angle, Spin;
        /// Pressed with the pointer it gives a little, then springs back.
        public double Scale = 1, ScaleTarget = 1;
        internal double ScaleVelocity;
        public double Opacity = 1;

        /// Where the peg is drawn, in strip coordinates.
        public double PivotX, PivotY;

        // In free flight: the centre of mass and its velocity.
        internal double Cx, Cy, Vx, Vy;
        /// Arriving, how long it still waits above the strip before falling.
        internal double Wait;

        // The card's shape, measured from the pivot: the distance down to
        // its centre of mass, and its moment of inertia per unit of mass
        // about the pivot.
        internal double Reach, Gyration, HalfWidth, FrameTop, Bottom;

        public Peg(double cardHeight, double weight)
        {
            Weight = weight;
            Reshape(cardHeight);
        }

        /// The card turned out a different height than it first measured.
        public void Reshape(double cardHeight)
        {
            double frame = cardHeight - PhotoCard.PegRow;
            HalfWidth = PhotoCard.CardWidth / 2;
            FrameTop = PhotoCard.PegRow - PhotoCard.RopeGrip;
            Bottom = cardHeight - PhotoCard.RopeGrip;
            Reach = FrameTop + frame / 2;
            // A thin plate about its centre, moved out to the pivot.
            Gyration = (PhotoCard.CardWidth * PhotoCard.CardWidth + frame * frame) / 12 + Reach * Reach;
        }

        /// From the pivot to a point fixed on the card, turned with it.
        internal (double X, double Y) Turned(double x, double y)
        {
            double c = Math.Cos(Angle), s = Math.Sin(Angle);
            return (x * c - y * s, x * s + y * c);
        }

        /// From the pivot to the centre of mass.
        internal (double X, double Y) ToCentre => Turned(0, Reach);

        /// How strongly this photo shows: fading along the rope as it
        /// leaves view, and in or out as it arrives or leaves.
        public double ShownOpacity
        {
            get
            {
                double p = Math.Clamp(Presence, 0, 1);
                return Opacity * p * p * (3 - 2 * p);
            }
        }

        internal bool OnRope(double width) => State == State.Hanging && !double.IsNaN(X) && X > 0 && X < width;
    }

    public List<Peg> Pegs { get; } = new();

    // The rope: its masses' heights below the knots, their velocities, and
    // this step's mass, force and acceleration at each.
    private double[] y = new double[2], v = new double[2], mass = new double[2], force = new double[2], acc = new double[2];
    private int segments = 1;
    private double width = 1, spacing = 1;
    /// Mass per DIP of rope, and of a photo of weight 1, in units that make
    /// the tension 1.
    private double density, photoMass;
    private double maxStep = 1.0 / 240;
    /// The wind blows on by the clock, whether or not anything is watching.
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private double time;

    public double Width
    {
        get => width;
        set
        {
            if (Math.Abs(value - width) < 0.5) return;
            width = value;
            segments = Math.Max(8, (int)Math.Round(width / NodeSpacing));
            spacing = width / segments;
            y = new double[segments + 1];
            v = new double[segments + 1];
            mass = new double[segments + 1];
            force = new double[segments + 1];
            acc = new double[segments + 1];

            // A uniform rope of tension 1 sags by ρgL²/8; a load m in the
            // middle pulls it down by mgL/4.
            density = 8 * EmptySag / (Gravity * width * width);
            photoMass = 4 * SagPerPhoto / (Gravity * width);
            // Waves run along it at √(T/ρ). An explicit step must not let
            // one skip past a mass.
            maxStep = 0.5 * spacing * Math.Sqrt(density);
            SettleNow();
        }
    }

    // MARK: The strip it hangs in

    /// How far the strip has slid down from its place, and how fast.
    public double StripY { get; set; }
    private double stripVelocity, stripTarget, stripFrequency = 12, stripDamping = 1;

    /// Slides the strip, rope and all, to `target`. Lowered, it overshoots a
    /// little and settles; raised, it just goes.
    public void MoveStrip(double target, bool lowering)
    {
        stripTarget = target;
        stripFrequency = lowering ? 12 : 17;
        stripDamping = lowering && !Motion.Reduced ? 0.74 : 1;
    }

    /// Puts the strip somewhere at once.
    public void PlaceStrip(double at)
    {
        StripY = stripTarget = at;
        stripVelocity = 0;
    }

    private bool StripResting => Math.Abs(StripY - stripTarget) < 0.3 && Math.Abs(stripVelocity) < 4;

    // MARK: Reading it

    /// Height of the rope at `x`, as it hangs right now.
    public double Y(double x)
    {
        if (x <= 0 || x >= width) return Top;
        var (i, f) = Locate(x);
        return Top + y[i] + (y[i + 1] - y[i]) * f;
    }

    /// Height of the rope at `x` once everything has come to rest.
    public double RestingY(double x)
    {
        double L = width;
        if (x <= 0 || x >= L) return Top;
        double drop = EmptySag * 4 * x * (L - x) / (L * L);
        // A unit load at `a` bends a span of length L by x(L − a)/L before
        // it, a(L − x)/L after it.
        double scale = 4 * SagPerPhoto / L;
        foreach (var peg in Pegs)
        {
            double a = peg.TargetX;
            if (peg.Gone || double.IsNaN(a) || a <= 0 || a >= L) continue;
            double moment = x < a ? x * (L - a) / L : a * (L - x) / L;
            drop += scale * peg.Weight * peg.TargetPresence * moment;
        }
        return Top + drop;
    }

    /// The lowest the rope hangs now or will hang at rest, anywhere.
    public double Lowest()
    {
        double lowest = Top;
        for (int i = 0; i <= segments; i++)
            lowest = Math.Max(lowest, Math.Max(Top + y[i], RestingY(i * spacing)));
        return lowest;
    }

    /// The rope as it hangs now, a point at each of its masses, or null if
    /// it has not visibly moved since last asked.
    public PointCollection? TakeTraceIfMoved()
    {
        if (traced.Length == y.Length)
        {
            double moved = 0;
            for (int i = 0; i < y.Length; i++) moved = Math.Max(moved, Math.Abs(y[i] - traced[i]));
            if (moved < 0.03) return null;
        }
        traced = (double[])y.Clone();
        var points = new PointCollection();
        for (int i = 1; i <= segments; i++) points.Add(new Point(i * spacing, Top + y[i]));
        return points;
    }

    private double[] traced = Array.Empty<double>();

    /// Nothing moves any more, and the air is still.
    public bool Settled { get; private set; }

    /// A draft is blowing along the line: time to move again.
    public bool WindStirring => !Motion.Reduced && Pegs.Any(peg =>
        peg.OnRope(width) && peg.ShownOpacity > 0.5 && Math.Abs(Wind.At(peg.X, Clock.Elapsed.TotalSeconds)) > StillAir);

    /// Wind weaker than this, as an acceleration, moves nothing visibly.
    private const double StillAir = 1.5;

    /// The rope is still bouncing, or a photo is in the air: it may be
    /// anywhere near where it rests.
    public bool Unsettled { get; private set; }

    /// A photo is falling in or away, outside where photos hang.
    public bool InFlight { get; private set; }

    private (int Index, double Fraction) Locate(double x)
    {
        double at = Math.Clamp(x / spacing, 0, segments - 1e-9);
        int i = (int)at;
        return (i, at - i);
    }

    // MARK: Changing it

    /// Puts everything where it would come to rest, at once: for when the
    /// line was out of sight while things changed. Photos still falling in
    /// keep falling.
    public void SettleNow()
    {
        foreach (var peg in Pegs)
        {
            if (peg.State != State.Hanging || double.IsNaN(peg.TargetX)) continue;
            peg.X = peg.TargetX;
            peg.Velocity = 0;
            peg.Presence = peg.TargetPresence;
            peg.Angle *= 0.3;
            peg.Spin = 0;
        }
        for (int i = 0; i <= segments; i++)
        {
            y[i] = RestingY(i * spacing) - Top;
            v[i] = 0;
        }
        UpdatePivots();
    }

    /// A photo that was already there: it hangs at rest from the start.
    public void HangNow(Peg peg)
    {
        peg.State = State.Hanging;
        if (!double.IsNaN(peg.TargetX)) peg.X = peg.TargetX;
        peg.Presence = peg.TargetPresence;
        peg.Opacity = 1;
        // Nobody hangs a photo perfectly straight.
        peg.Angle = (Random.Shared.NextDouble() - 0.5) * 0.03;
    }

    /// A new photo: it falls in from above, a little askew, and the rope
    /// catches it. With less motion it just fades in where it hangs. One
    /// `later` seconds behind the others waits that long, out of sight,
    /// before it lets go: each lands as gently as the first.
    public void DropIn(Peg peg, double later = 0)
    {
        HangNow(peg);
        if (Motion.Reduced || peg.TargetPresence < 1)
        {
            peg.Opacity = 0;
            return;
        }
        peg.State = State.Arriving;
        peg.Angle = (Random.Shared.Next(2) == 0 ? -1 : 1) * (0.1 + 0.1 * Random.Shared.NextDouble());
        peg.Spin = -peg.Angle * 1.5;
        var (cx, cy) = peg.ToCentre;
        // From just above the top of the strip, already falling. If the
        // strip is still sliding down, it comes down above it.
        peg.Cx = peg.TargetX + cx;
        peg.Cy = -peg.Bottom - 10 - Math.Max(0, StripY) + cy;
        peg.Wait = later;
        peg.Vx = 0;
        peg.Vy = 450;
        peg.Opacity = 0.2;
    }

    /// Taken off the line: the peg lets go, the photo drops away and the
    /// rope, relieved of it, springs up.
    public void Release(Peg peg)
    {
        if (peg.State == State.Leaving) return;
        bool unseen = peg.ShownOpacity < 0.02 || double.IsNaN(peg.X);
        if (peg.State == State.Hanging && !unseen)
        {
            double py = Y(peg.X), pv = RopeVelocity(peg.X);
            var (rx, ry) = peg.ToCentre;
            // The centre of mass moves with the pivot and with the swing.
            peg.Cx = peg.X + rx;
            peg.Cy = py + ry;
            peg.Vx = peg.Velocity - peg.Spin * ry;
            // Opening the peg lifts it off the rope a touch.
            peg.Vy = pv - 160 + peg.Spin * rx;
            peg.Spin += (Random.Shared.NextDouble() - 0.5) * 1.2;
        }
        peg.State = State.Leaving;
        if (unseen) peg.Removed = true;
    }

    /// The pointer moving through the strip at `p` with velocity `velocity`:
    /// it brushes any photo it passes over, and plucks the rope if it
    /// crosses it.
    public void Brush(Point p, Point velocity, double dt)
    {
        if (Motion.Reduced || dt <= 0) return;
        // Contact acts for as long as the pointer moves; this is its share
        // of a grip that would match speeds in about 1/20 s.
        double grip = 1 - Math.Exp(-dt / 0.05);

        foreach (var peg in Pegs)
        {
            if (!peg.OnRope(width) || peg.ShownOpacity < 0.5) continue;
            // The pointer in the card's own frame.
            double dx = p.X - peg.PivotX, dy = p.Y - peg.PivotY;
            double c = Math.Cos(-peg.Angle), s = Math.Sin(-peg.Angle);
            double lx = dx * c - dy * s, ly = dx * s + dy * c;
            if (Math.Abs(lx) > peg.HalfWidth || ly < peg.FrameTop || ly > peg.Bottom) continue;

            // How fast that spot on the card moves, and how much faster the
            // pointer does. Their difference becomes a push at that spot.
            double pointX = peg.Velocity - peg.Spin * dy, pointY = RopeVelocity(peg.X) + peg.Spin * dx;
            double relX = Math.Clamp(velocity.X, -BrushSpeed, BrushSpeed) - pointX;
            double relY = Math.Clamp(velocity.Y, -BrushSpeed, BrushSpeed) - pointY;
            double torque = dx * relY - dy * relX;
            double spin = peg.Spin + BrushGrip * grip * torque / peg.Gyration;
            // Never pushed past the limit, though it can be slowed from beyond it.
            double limit = Math.Max(MaxBrushSpin, Math.Abs(peg.Spin));
            peg.Spin = Math.Clamp(spin, -limit, limit);
        }

        // Crossing the rope plucks it.
        if (p.X > 0 && p.X < width && Math.Abs(p.Y - Y(p.X)) < 7)
        {
            double at = p.X / spacing;
            for (int i = Math.Max(1, (int)at - 2); i <= Math.Min(segments - 1, (int)at + 3); i++)
            {
                double near = Math.Exp(-Math.Pow((i - at) / 1.2, 2));
                v[i] += PluckGrip * grip * near * (Math.Clamp(velocity.Y, -1500, 1500) - v[i]) * density * spacing / mass[i];
            }
        }
    }

    /// A click on the photo at `local`, a point in the card's own frame
    /// measured from its top left: it is pressed back for a moment, which
    /// dips the rope and tips the photo towards the side that was pressed.
    public void Poke(Peg peg, Point local)
    {
        if (Motion.Reduced || !peg.OnRope(width)) return;
        var (rx, _) = peg.Turned(local.X - peg.HalfWidth, local.Y - PhotoCard.RopeGrip);
        const double push = 200;
        peg.Spin = Math.Clamp(peg.Spin + rx * push * 0.5 / peg.Gyration, -MaxSpin, MaxSpin);
        Kick(peg.X, peg.Weight * photoMass * push * 0.35);
    }

    /// Gives the rope at `x` a downward impulse.
    private void Kick(double x, double impulse)
    {
        var (i, f) = Locate(x);
        if (i > 0) v[i] += impulse * (1 - f) / mass[i];
        if (i + 1 < segments) v[i + 1] += impulse * f / mass[i + 1];
    }

    private double RopeVelocity(double x)
    {
        if (x <= 0 || x >= width) return 0;
        var (i, f) = Locate(x);
        return v[i] + (v[i + 1] - v[i]) * f;
    }

    // MARK: Moving it on

    /// Moves everything on by `elapsed` seconds.
    public void Step(double elapsed)
    {
        // A long frame (the window was hidden, say) must not fling anything.
        elapsed = Math.Clamp(elapsed, 0, 1.0 / 20);
        if (elapsed == 0) return;
        bool reduced = Motion.Reduced;
        time = Clock.Elapsed.TotalSeconds - elapsed;
        int steps = Math.Max(1, (int)Math.Ceiling(elapsed / maxStep));
        double dt = elapsed / steps;
        for (int s = 0; s < steps; s++) Substep(dt, reduced);

        Collide();
        Pegs.RemoveAll(peg => peg.Removed);
        UpdatePivots();
        Assess();
    }

    private void Substep(double dt, bool reduced)
    {
        time += dt;

        // The strip slides on a spring. Its acceleration is felt by
        // everything in it, as in a lift; a little of it, since the photos'
        // real peers would never be thrown about this hard.
        double stripAcceleration = stripFrequency * stripFrequency * (stripTarget - StripY)
                                   - 2 * stripDamping * stripFrequency * stripVelocity;
        stripVelocity += stripAcceleration * dt;
        StripY += stripVelocity * dt;
        double felt = Math.Clamp(0.5 * stripAcceleration, -1.2 * Gravity, 0.45 * Gravity);
        double gravity = Gravity - felt;

        foreach (var peg in Pegs)
        {
            if (double.IsNaN(peg.X)) continue;
            if (peg.Presence != peg.TargetPresence)
            {
                double step = PresenceRate * dt;
                peg.Presence = peg.Presence < peg.TargetPresence
                    ? Math.Min(peg.TargetPresence, peg.Presence + step)
                    : Math.Max(peg.TargetPresence, peg.Presence - step);
            }
            peg.Acceleration = SlideStiffness * (peg.TargetX - peg.X) - SlideDamping * peg.Velocity;
            peg.Velocity += peg.Acceleration * dt;
            peg.X += peg.Velocity * dt;

            double scaleAcceleration = 900 * (peg.ScaleTarget - peg.Scale) - (reduced ? 60 : 26) * peg.ScaleVelocity;
            peg.ScaleVelocity += scaleAcceleration * dt;
            peg.Scale += peg.ScaleVelocity * dt;

            if (peg.State == State.Leaving) peg.Opacity = Math.Max(0, peg.Opacity - dt / DropFade);
            else if (peg.Opacity < 1) peg.Opacity = Math.Min(1, peg.Opacity + dt / 0.2);
        }

        // Each mass of rope weighs its share of the rope, plus its share of
        // the photos hanging next to it. A swinging photo pulls harder at
        // the bottom of its swing, as a child on a swing does.
        double nodeMass = density * spacing;
        for (int i = 0; i <= segments; i++)
        {
            mass[i] = nodeMass;
            force[i] = 0;
        }
        foreach (var peg in Pegs)
        {
            if (!peg.OnRope(width)) continue;
            double m = peg.Weight * peg.Presence * photoMass;
            if (m <= 0) continue;
            var (i, f) = Locate(peg.X);
            double tug = m * peg.Reach * peg.Spin * peg.Spin * Math.Cos(peg.Angle);
            mass[i] += (1 - f) * m;
            mass[i + 1] += f * m;
            force[i] += (1 - f) * tug;
            force[i + 1] += f * tug;
        }

        double drag = reduced ? 14 : RopeDrag;
        acc[0] = acc[segments] = 0;
        for (int i = 1; i < segments; i++)
        {
            // The tension pulls each mass towards the line between its
            // neighbours; friction resists them moving apart.
            double tension = (y[i - 1] - 2 * y[i] + y[i + 1]) / spacing;
            double friction = RopeFriction * (v[i - 1] - 2 * v[i] + v[i + 1]) / spacing;
            acc[i] = (tension + friction + force[i]) / mass[i] + gravity - drag * v[i];
        }
        for (int i = 1; i < segments; i++)
        {
            v[i] += acc[i] * dt;
            y[i] += v[i] * dt;
        }

        foreach (var peg in Pegs)
        {
            if (double.IsNaN(peg.X)) continue;
            switch (peg.State)
            {
                case State.Hanging:
                    Swing(peg, dt, gravity, reduced);
                    break;
                case State.Arriving:
                    if (peg.Wait > 0) { peg.Wait -= dt; break; }
                    Fly(peg, dt, gravity);
                    var (rx, ry) = peg.ToCentre;
                    double px = peg.Cx - rx, py = peg.Cy - ry;
                    if (peg.Vy > 0 && py >= Y(px)) Catch(peg, px);
                    break;
                case State.Leaving:
                    if (!reduced) Fly(peg, dt, gravity);
                    if (peg.Opacity <= 0) peg.Removed = true;
                    break;
            }
        }
    }

    /// A pendulum whose pivot accelerates: in the pivot's frame, gravity
    /// tilts and strengthens or weakens with the pivot's motion, and the
    /// photo swings towards wherever that apparent gravity points.
    private void Swing(Peg peg, double dt, double gravity, bool reduced)
    {
        double sin = Math.Sin(peg.Angle), cos = Math.Cos(peg.Angle);
        double alpha;
        if (reduced)
        {
            // Back to hanging straight, without overshooting.
            double w = Math.Sqrt(Gravity * peg.Reach / peg.Gyration);
            alpha = -w * w * peg.Angle - 2 * w * peg.Spin;
        }
        else
        {
            // The peg sliding along, and the rope moving up and down. Both
            // are felt through the peg's grip, which gives a little, and
            // never more strongly than a real peg on a real line would be
            // jerked about.
            double ay = 0;
            if (peg.X > 0 && peg.X < width)
            {
                var (i, f) = Locate(peg.X);
                ay = acc[i] + (acc[i + 1] - acc[i]) * f;
            }
            double ease = 1 - Math.Exp(-dt / GripLag);
            peg.FeltX += (Math.Clamp(SlideFelt * peg.Acceleration, -MaxSideways, MaxSideways) - peg.FeltX) * ease;
            peg.FeltY += (Math.Clamp(ay, -MaxUpDown, MaxUpDown) - peg.FeltY) * ease;
            double ax = peg.FeltX;
            ay = peg.FeltY;
            // Wind pushes on the card's middle.
            double wind = Wind.At(peg.X, time) * peg.Presence;
            alpha = peg.Reach / peg.Gyration * ((ax - wind) * cos - (gravity - ay) * sin)
                    - SwingDrag * peg.Spin - SwingDragQuadratic * peg.Spin * Math.Abs(peg.Spin);
        }
        peg.Spin += alpha * dt;
        peg.Angle += peg.Spin * dt;
    }

    /// Free flight: the centre of mass falls, the card turns about it.
    private static void Fly(Peg peg, double dt, double gravity)
    {
        peg.Vy += gravity * dt;
        // Air resistance, mostly sideways for a falling card.
        peg.Vx -= 1.5 * peg.Vx * dt;
        peg.Cx += peg.Vx * dt;
        peg.Cy += peg.Vy * dt;
        peg.Spin -= 0.6 * peg.Spin * dt;
        peg.Angle += peg.Spin * dt;
    }

    /// The falling photo's peg meets the rope. The rope takes up part of its
    /// momentum, and the jolt of the pivot stopping sets the photo swinging.
    private void Catch(Peg peg, double px)
    {
        var (rx, ry) = peg.ToCentre;
        // How fast the pivot was falling.
        double pivotFall = peg.Vy - peg.Spin * rx;
        double rope = RopeVelocity(px);
        double caught = rope + CatchTransfer * (pivotFall - rope);

        double m = peg.Weight * peg.Presence * photoMass;
        var (i, f) = Locate(px);
        if (i > 0) v[i] = (mass[i] * v[i] + (1 - f) * m * caught) / (mass[i] + (1 - f) * m);
        if (i + 1 < segments) v[i + 1] = (mass[i + 1] * v[i + 1] + f * m * caught) / (mass[i + 1] + f * m);

        // The centre of mass carries on where the pivot stopped: off to one
        // side of it, that turns the photo.
        double stopped = pivotFall - caught;
        peg.Spin = Math.Clamp(peg.Spin + stopped * rx / peg.Gyration, -MaxSpin, MaxSpin);
        peg.X = px;
        peg.Velocity = 0;
        peg.State = State.Hanging;
    }

    /// Neighbouring photos swinging into each other bounce apart.
    private void Collide()
    {
        Peg? left = null;
        foreach (var peg in Pegs.Where(p => p.OnRope(width) && p.ShownOpacity > 0.3).OrderBy(p => p.X))
        {
            if (left is not null && peg.X - left.X < 2 * (left.HalfWidth + left.Bottom)) Knock(left, peg);
            left = peg;
        }
    }

    private void Knock(Peg a, Peg b)
    {
        // The corner of each that reaches furthest towards the other.
        var (ax, ay) = Corner(a, right: true);
        var (bx, by) = Corner(b, right: false);
        double ayRope = Y(a.X), byRope = Y(b.X);
        double overlap = (a.X + ax) - (b.X + bx);
        if (overlap <= 0) return;
        // Only if the corners are level with the other photo.
        if (ayRope + ay < byRope + b.FrameTop || ayRope + ay > byRope + b.Bottom) return;
        // Corners level with the pivots cannot push the swing either way.
        if (Math.Abs(ay) < 20 || Math.Abs(by) < 20) return;

        // Pivots hold firm sideways, so only the swing gives.
        double ka = ay * ay / (a.Gyration * a.Weight), kb = by * by / (b.Gyration * b.Weight);
        double closing = (a.Velocity - a.Spin * ay) - (b.Velocity - b.Spin * by);
        if (closing > 0)
        {
            double impulse = (1 + Restitution) * closing / (ka + kb);
            a.Spin += ay * impulse / (a.Gyration * a.Weight);
            b.Spin -= by * impulse / (b.Gyration * b.Weight);
        }
        // And they never stay inside each other: eased apart a little each frame.
        double share = Math.Min(overlap, 6) / (ka + kb) * 0.5;
        a.Angle += Math.Clamp(ay * share / (a.Gyration * a.Weight), -0.03, 0.03);
        b.Angle -= Math.Clamp(by * share / (b.Gyration * b.Weight), -0.03, 0.03);
    }

    private static (double X, double Y) Corner(Peg peg, bool right)
    {
        double side = right ? peg.HalfWidth : -peg.HalfWidth;
        var top = peg.Turned(side, peg.FrameTop);
        var bottom = peg.Turned(side, peg.Bottom);
        return right ? (top.X > bottom.X ? top : bottom) : (top.X < bottom.X ? top : bottom);
    }

    /// Where each peg is drawn: on the rope, or wherever it flies.
    private void UpdatePivots()
    {
        foreach (var peg in Pegs)
        {
            if (double.IsNaN(peg.X)) continue;
            if (peg.State == State.Hanging)
            {
                peg.PivotX = peg.X;
                peg.PivotY = Y(peg.X);
            }
            else
            {
                var (rx, ry) = peg.ToCentre;
                peg.PivotX = peg.Cx - rx;
                peg.PivotY = peg.Cy - ry;
            }
        }
    }

    private void Assess()
    {
        double fastest = 0, furthest = 0;
        for (int i = 1; i < segments; i++)
        {
            fastest = Math.Max(fastest, Math.Abs(v[i]));
            furthest = Math.Max(furthest, Math.Abs(Top + y[i] - RestingY(i * spacing)));
        }
        InFlight = Pegs.Any(p => p.State != State.Hanging);
        bool pegsMoving = Pegs.Any(p => !double.IsNaN(p.X) && (
            Math.Abs(p.X - p.TargetX) > 0.1 || Math.Abs(p.Velocity) > 0.5
            || p.Presence != p.TargetPresence || p.Opacity < 1
            || Math.Abs(p.Scale - p.ScaleTarget) > 0.001 || Math.Abs(p.ScaleVelocity) > 0.01));
        // Still: barely turning, and hanging as straight as it will.
        bool swinging = Pegs.Any(p => Math.Abs(p.Spin) > 0.004 || Math.Abs(p.Angle) > 0.004);
        Unsettled = InFlight || fastest > 3 || furthest > 1.5 || !StripResting;
        Settled = !Unsettled && fastest < 0.5 && !pegsMoving && !swinging && !WindStirring;
    }
}

/// Drafts blowing along the line. Each gust reaches the photos further
/// along a moment later, so a ripple of motion travels down the line.
/// Between spells of breeze the air is still and the photos come to rest.
public static class Wind
{
    /// How fast gusts travel along the line, in DIPs a second.
    private const double Speed = 480;
    /// The push of a full gust, as an acceleration on a photo.
    private const double Strength = 32;

    /// The wind's push at `x` at `time`, rightwards.
    public static double At(double x, double time)
    {
        double s = time - x / Speed;
        // Indoors a draft comes and goes: spells of breeze some seconds
        // long, and still air between them.
        double spell = Math.Max(0, Noise(s * 0.07 + 7.3) + 0.15) / 1.15;
        if (spell <= 0) return 0;
        double gust = 0.6 * Noise(s * 0.35) + 0.4 * Noise(s * 0.9 + 19.1);
        // A little turbulence of its own at every photo.
        double eddy = 0.25 * Noise(time * 1.4 + x * 0.011 + 41.7);
        // Faded in and out smoothly at the edges of a spell.
        double strength = spell * spell * (3 - 2 * Math.Min(1, spell));
        return Strength * 1.6 * strength * (0.45 + gust + eddy);
    }

    /// Smooth noise between −1 and 1.
    private static double Noise(double x)
    {
        double floor = Math.Floor(x);
        int i = (int)floor;
        double f = x - floor;
        double fade = f * f * f * (f * (f * 6 - 15) + 10);
        double a = Gradient(i) * f, b = Gradient(i + 1) * (f - 1);
        return 2 * (a + (b - a) * fade);
    }

    private static double Gradient(int i)
    {
        uint h = unchecked((uint)i * 0x27d4eb2du);
        h ^= h >> 15;
        h = unchecked(h * 0x85ebca6bu);
        h ^= h >> 13;
        return (h & 0xffff) / 32767.5 - 1;
    }
}
