using ComputerAlgebra;
using System.Collections.Generic;
using System.ComponentModel;

namespace Circuit
{
    /// <summary>
    /// Ideal transformer.
    /// </summary>
    [Category("Generic")]
    [DisplayName("Center Tap Transformer")]
    [DefaultProperty("Turns")]
    [Description("Ideal transformer with a center tap in the secondary coil.")]
    public class CenterTapTransformer : Component
    {
        private Terminal pa, pc, sa, st, sc;

        public override IEnumerable<Terminal> Terminals
        {
            get
            {
                yield return pa;
                yield return pc;
                yield return sa;
                yield return st;
                yield return sc;
            }
        }

        protected Ratio turns = new Ratio(1, 1);
        [Serialize, Description("Primary:secondary turns ratio.")]
        public Ratio Turns { get { return turns; } set { turns = value; NotifyChanged(nameof(Turns)); } }

        public CenterTapTransformer()
        {
            pa = new Terminal(this, "PA");
            pc = new Terminal(this, "PC");
            sa = new Terminal(this, "SA");
            st = new Terminal(this, "ST");
            sc = new Terminal(this, "SC");
            Name = "TX1";
        }

        public override void Analyze(Analysis Mna)
        {
            Expression Ip = Mna.AddUnknown("i" + Name + "p");
            Mna.AddPassiveComponent(pa, pc, Ip);
            Expression Isa = Mna.AddUnknown("i" + Name + "sa");
            Expression Isc = Mna.AddUnknown("i" + Name + "sc");
            Mna.AddTerminal(sa, -Isa);
            Mna.AddTerminal(sc, Isc);
            Mna.AddTerminal(st, Isa - Isc);
            // EMITTER FORK: fix the ampere-turns equation's missing factor of 2.
            // Was `Ip * turns == Isa + Isc`, which is off by a factor of
            // two and silently violates energy conservation (power delivered to a resistive
            // secondary load comes out at 2x the power drawn from the primary, for ANY load,
            // independent of `turns` -- verified both by hand from these equations and by an
            // isolated A/B render of a real amp circuit through this solver, which measured a
            // consistent ~4.8 dB level increase from clean tone through hard clipping when this
            // component is used instead of two plain Transformers).
            //
            // `turns` here is Np : Ns_total (the primary-to-FULL-secondary ratio -- see the two
            // voltage equations below, which correctly relate Vp to each HALF-secondary voltage
            // via `turns * 2`, i.e. they already encode Ns_half = Ns_total / 2). Ampere-turns
            // conservation for a winding split symmetrically in half is
            //     Np * Ip = Ns_half * (Isa + Isc) = (Ns_total / 2) * (Isa + Isc)
            // which in terms of `turns` (= Np / Ns_total) is
            //     Ip * turns = (Isa + Isc) / 2   <=>   Ip * turns * 2 = Isa + Isc
            // i.e. the SAME `* 2` factor the voltage equations already use, restoring the
            // symmetry between the current and voltage relations that a correct ideal
            // center-tapped transformer has.
            Mna.AddEquation(Ip * turns * 2, Isa + Isc);

            Expression Vp = pa.V - pc.V;
            Expression Vs1 = sa.V - st.V;
            Expression Vs2 = st.V - sc.V;
            Mna.AddEquation(Vp, Vs1 * turns * 2);
            Mna.AddEquation(Vp, Vs2 * turns * 2);
        }

        protected internal override void LayoutSymbol(SymbolLayout Sym)
        {
            int h = 20;

            Sym.AddTerminal(pa, new Coord(-10, h));
            Sym.AddTerminal(pc, new Coord(-10, -h));
            Sym.AddTerminal(sa, new Coord(10, h));
            Sym.AddTerminal(st, new Coord(10, 0));
            Sym.AddTerminal(sc, new Coord(10, -h));

            Sym.DrawText(() => Name, new Coord(-16, -h / 2), Alignment.Far, Alignment.Center);
            Sym.DrawText(() => Turns.ToString(), new Coord(-16, h / 2), Alignment.Far, Alignment.Center);

            h -= 4;

            Sym.AddWire(pa, new Coord(-10, h));
            Sym.AddWire(pc, new Coord(-10, -h));
            Sym.AddWire(sa, new Coord(10, h));
            Sym.AddWire(sc, new Coord(10, -h));
            Sym.InBounds(new Coord(-20, 0), new Coord(20, 0));

            Inductor.Draw(Sym, -10, -h, h, 4, 4.0);
            Sym.DrawLine(EdgeType.Black, new Coord(-2, h), new Coord(-2, -h));
            Sym.DrawLine(EdgeType.Black, new Coord(2, h), new Coord(2, -h));
            Inductor.Draw(Sym, 10, -h, 0.0, 2, -4.0);
            Inductor.Draw(Sym, 10, h, 0.0, 2, -4.0);
        }
    }
}
