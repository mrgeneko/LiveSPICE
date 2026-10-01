using ComputerAlgebra;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Circuit.Components
{
    /// <summary>
    /// Which equation computes the pentode's plate and screen current. Both share the same parameters
    /// (Mu, Ex, Kg1, Kg2, Kp, Kvb) but a parameter set is only valid for the equation it was fitted with.
    /// </summary>
    public enum PentodeModel
    {
        /// <summary>
        /// The original equation: E1 = Vpk/Kp * ln(1+exp(Kp*(1/Mu + Vgk/sqrt(Kvb + Vg2k^2)))), i.e. scaled by the
        /// PLATE voltage, so plate current rises ~Vpk^Ex with no saturation above the knee. The default, so every
        /// existing schematic renders exactly as before.
        /// </summary>
        PlateScaled,

        /// <summary>
        /// Koren's published tetrode form: E1 = Vg2k/Kp * ln(1+exp(Kp*(1/Mu + Vgk/Vg2k))), scaled by the SCREEN
        /// voltage. Fits datasheet currents at Vp ~ Vs and at Vs below Vp, and gives realistic plate current at low
        /// plate voltage. Vg2k is floored at 1 V so the expression stays defined with the screen at or below the cathode.
        /// </summary>
        Koren,
    }

    [Category("Vacuum Tubes")]
    [DisplayName("Pentode")]
    public class Pentode : Component
    {
        private Terminal _plate, _grid, _grid2, _cathode;

        private PentodeModel _model = PentodeModel.PlateScaled;
        [Serialize, Description("Equation used for plate and screen current. A parameter set is only valid for the model it was fitted with.")]
        public PentodeModel Model { get { return _model; } set { _model = value; NotifyChanged(nameof(Model)); } }

        private double _mu = 10.7;
        [Serialize, Category("Koren"), Description("Voltage gain.")]
        public double Mu { get { return _mu; } set { _mu = value; NotifyChanged(nameof(Mu)); } }

        private double _kg1 = 1672.0;
        [Serialize, Category("Koren"), Browsable(true)]
        public double Kg1 { get { return _kg1; } set { _kg1 = value; NotifyChanged(nameof(Kg1)); } }

        private double _kg2 = 4500;
        [Serialize, Category("Koren"), Browsable(true)]
        public double Kg2 { get { return _kg2; } set { _kg2 = value; NotifyChanged(nameof(Kg2)); } }

        private double _kp = 41.16;
        [Serialize, Category("Koren"), Browsable(true)]
        public double Kp { get { return _kp; } set { _kp = value; NotifyChanged(nameof(Kp)); } }

        private double _kvb = 12.7;
        [Serialize, Category("Koren"), Browsable(true)]
        public double Kvb { get { return _kvb; } set { _kvb = value; NotifyChanged(nameof(Kvb)); } }

        private double _ex = 1.310;
        [Serialize, Category("Koren"), Browsable(true)]
        public double Ex { get { return _ex; } set { _ex = value; NotifyChanged(nameof(Ex)); } }

        private Quantity rgk = new Quantity(2e4, Units.Ohm);
        [Serialize, Category("Koren"), Description("Grid resistance")]
        public Quantity Rgk { get { return rgk; } set { if (rgk.Set(value)) NotifyChanged(nameof(Rgk)); } }

        private Quantity kn = new Quantity(3, Units.V);
        [Serialize, Category("Koren"), Description("Knee size")]
        public Quantity Kn { get { return kn; } set { if (kn.Set(value)) NotifyChanged(nameof(Kn)); } }

        private Quantity vg = new Quantity(13, Units.V);
        [Serialize, Category("Koren")]
        public Quantity Vg { get { return vg; } set { if (vg.Set(value)) NotifyChanged(nameof(Vg)); } }

        // EMITTER FORK: interelectrode capacitance, ported from Triode's SimulateCapacitances.
        // Only control-grid/plate/cathode caps are modeled (no screen-grid terms) -- matches
        // what fitted datasheet data is normally available for beam-power/pentode output tubes.
        // Opt-in, defaults false: existing circuits are unaffected until they set this true.
        private bool simulateCapacitances;
        [Serialize, Category("Koren")]
        public bool SimulateCapacitances { get { return simulateCapacitances; } set { simulateCapacitances = value; NotifyChanged(nameof(SimulateCapacitances)); } }

        private Quantity _cgp = new Quantity(2.4e-12m, Units.F);
        [Serialize, Description("Grid to plate capacitance.")]
        public Quantity Cgp { get { return _cgp; } set { _cgp = value; NotifyChanged(nameof(Cgp)); } }

        private Quantity _cgk = new Quantity(2.3e-12m, Units.F);
        [Serialize, Description("Grid to cathode capacitance.")]
        public Quantity Cgk { get { return _cgk; } set { _cgk = value; NotifyChanged(nameof(Cgk)); } }

        private Quantity _cpk = new Quantity(9e-13m, Units.F);
        [Serialize, Description("Plate to cathode capacitance.")]
        public Quantity Cpk { get { return _cpk; } set { _cpk = value; NotifyChanged(nameof(Cpk)); } }

        public Pentode()
        {
            _plate = new Terminal(this, "P");
            _grid = new Terminal(this, "G");
            _grid2 = new Terminal(this, "G2");
            _cathode = new Terminal(this, "K");
        }

        public override IEnumerable<Terminal> Terminals
        {
            get
            {
                yield return _plate;
                yield return _grid;
                yield return _grid2;
                yield return _cathode;
            }
        }

        protected internal override void LayoutSymbol(SymbolLayout Sym)
        {
            Sym.AddTerminal(_plate, new Coord(0, 25), new Coord(0, 10));
            Sym.AddWire(new Coord(-10, 10), new Coord(10, 10));

            Sym.AddTerminal(_grid, new Coord(-20, -5), new Coord(-12, -5));
            Sym.AddTerminal(_grid2, new Coord(20, 0), new Coord(12, 0));
            for (int i = -10; i < 10; i += 8)
            {
                Sym.AddWire(new Coord(i, -5), new Coord(i + 4, -5));
                Sym.AddWire(new Coord(i, 0), new Coord(i + 4, 0));
                Sym.AddWire(new Coord(i, 5), new Coord(i + 4, 5));

            }
            Sym.AddTerminal(_cathode, new Coord(-10, -25), new Coord(-10, -12), new Coord(-8, -10), new Coord(8, -10), new Coord(10, -12));

            Sym.DrawArc(EdgeType.Black, new Coord(0, 5), 20d, 0, Math.PI, Direction.Counterclockwise);
            Sym.DrawArc(EdgeType.Black, new Coord(0, -5), 20d, 0, Math.PI);
            Sym.AddLine(EdgeType.Black, new Coord(-20, -5), new Coord(-20, 5));
            Sym.AddLine(EdgeType.Black, new Coord(20, -5), new Coord(20, 5));


            if (PartNumber != null)
                Sym.DrawText(() => PartNumber, new Coord(-2, 25), Alignment.Far, Alignment.Near);
            Sym.DrawText(() => Name, new Point(-8, -25), Alignment.Near, Alignment.Far);

        }

        public override void Analyze(Analysis Mna)
        {
            var vpk = _plate.V - _cathode.V;
            var vgk = _grid.V - _cathode.V;
            var vg2k = _grid2.V - _cathode.V;

            Expression E1;
            switch (_model)
            {
                case PentodeModel.Koren:
                    var vs = Call.If(vg2k > 1.0, vg2k, 1.0);
                    E1 = vs / Kp * Ln1Exp(Kp * ((1.0 / Mu) + (vgk / vs)));
                    break;
                default:    // PentodeModel.PlateScaled: the original equation, unchanged
                    E1 = vpk / Kp * Ln1Exp(Kp * ((1.0 / Mu) + (vgk * Binary.Power(Kvb + vg2k * vg2k, -.5))));
                    break;
            }
            var iKoren = Call.If(E1 > 0, Binary.Power(E1, Ex), 0);
            var ip = Call.If(vpk > 0, iKoren / Kg1 * Call.ArcTan(vpk / Kvb), 0);

            var vg = (Real)Vg;
            var knee = (Real)Kn;
            var rg1 = (Real)Rgk;

            var a = 1 / (4 * knee * rg1);
            var b = ((Expression)Kn - Vg) / (2 * knee * rg1);
            var c = (-a * Binary.Power(vg - knee, 2)) - (b * (vg - knee));

            var ig = Call.If(vgk < vg - knee, 0, Call.If(vgk > vg + knee, (vgk - vg) / rg1, a * vgk * vgk + b * vgk + c));
            var ig2 = iKoren / Kg2;
            var ik = -(ip + ig + ig2);

            if (SimulateCapacitances)
            {
                Capacitor.Analyze(Mna, Name + "_cgp", _plate, _grid, _cgp);
                Capacitor.Analyze(Mna, Name + "_cgk", _grid, _cathode, _cgk);
                Capacitor.Analyze(Mna, Name + "_cpk", _plate, _cathode, _cpk);
            }

            Mna.AddTerminal(_plate, ip);
            Mna.AddTerminal(_grid, ig);
            Mna.AddTerminal(_grid2, ig2);
            Mna.AddTerminal(_cathode, ik);
        }
        private static Expression Ln1Exp(Expression x)
        {
            return Call.If(x > 50, x, Call.Ln(1 + Call.Exp(x)));
        }
    }
}
