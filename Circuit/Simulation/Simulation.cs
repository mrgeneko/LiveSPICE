using ComputerAlgebra;
using ComputerAlgebra.LinqCompiler;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Util;
using LinqExpr = System.Linq.Expressions.Expression;
using ParamExpr = System.Linq.Expressions.ParameterExpression;

namespace Circuit
{
    /// <summary>
    /// Exception thrown when a simulation does not converge.
    /// </summary>
    /// <summary>
    /// Newton-solver diagnostics for a Simulation. The counters are always on (inline locals in the generated
    /// code, flushed once per Run chunk and on divergence); the per-iteration trace is opt-in (TraceNewton).
    /// A solve is "unconverged" when its iteration cap ran out before the step test passed.
    /// </summary>
    public class NewtonStats
    {
        public long Solves, Exhausted, Severe, FirstExhausted = -1, LastExhausted = -1, SamplesProcessed;
        public double MaxAbsOutput;
        public string[] UnknownNames;
        public int TraceLimit;
        public List<Tuple<long, double[]>> Traces = new List<Tuple<long, double[]>>();
        private readonly double[] tbuf = new double[6 * 16384];
        private int tn;

        public void Reset()
        {
            Solves = Exhausted = Severe = SamplesProcessed = 0; FirstExhausted = LastExhausted = -1; MaxAbsOutput = 0;
            Traces.Clear(); tn = 0;
        }

        public void Add(long solves, long exhausted, long severe, long first, long last, double maxAbs, int samples)
        {
            Solves += solves; Exhausted += exhausted; Severe += severe;
            if (first >= 0 && FirstExhausted < 0) FirstExhausted = first;
            if (last >= 0) LastExhausted = last;
            if (maxAbs > MaxAbsOutput) MaxAbsOutput = maxAbs;
            SamplesProcessed += samples;
        }

        public void TraceIter(int iteration, double maxDv, double maxV, int dominant, double dvDominant, double vDominant)
        {
            if (tn >= 16384) return;
            int o = tn * 6;
            tbuf[o] = iteration; tbuf[o + 1] = maxDv; tbuf[o + 2] = maxV; tbuf[o + 3] = dominant; tbuf[o + 4] = dvDominant; tbuf[o + 5] = vDominant;
            ++tn;
        }

        public void EndSolve(long sample, int exhausted)
        {
            if (exhausted != 0 && Traces.Count < TraceLimit)
            {
                var copy = new double[tn * 6];
                Array.Copy(tbuf, copy, tn * 6);
                Traces.Add(Tuple.Create(sample, copy));
            }
            tn = 0;
        }

        public string Summary()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "newton: solves={0} unconverged={1} ({2:F4}%) severe={6} first_sample={3} last_sample={4} max_abs_output={5:G4}",
                Solves, Exhausted, 100.0 * Exhausted / Math.Max(Solves, 1), FirstExhausted, LastExhausted, MaxAbsOutput, Severe);
        }

        public IEnumerable<string> TraceLines()
        {
            foreach (var tr in Traces)
            {
                double[] a = tr.Item2; int cnt = a.Length / 6; int flips = 0;
                for (int q = 1; q < cnt; q++)
                    if (Math.Sign(a[q * 6 + 4]) != Math.Sign(a[(q - 1) * 6 + 4])) flips++;
                yield return "newton-trace: solve at sample " + tr.Item1 + ", iterations=" + cnt + ", dominant-step sign flips=" + flips + "/" + Math.Max(cnt - 1, 0);
                for (int q = 0; q < cnt; q++)
                {
                    if (!(q < 12 || q % 256 == 0 || q >= cnt - 6)) continue;
                    int ai = (int)a[q * 6 + 3];
                    string name = (UnknownNames != null && ai >= 0 && ai < UnknownNames.Length) ? UnknownNames[ai] : "?";
                    yield return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "newton-trace:   it={0} max|dv|={1:E3} max|v|={2:E3} dominant={3} dv={4:E3} v={5:E3}",
                        (int)a[q * 6], a[q * 6 + 1], a[q * 6 + 2], name, a[q * 6 + 4], a[q * 6 + 5]);
                }
            }
        }
    }

    public class SimulationDiverged : FailedToConvergeException
    {
        private long at;
        /// <summary>
        /// Sample number at which the simulation diverged.
        /// </summary>
        public long At { get { return at; } }

        public SimulationDiverged(string Message, long At) : base(Message) { at = At; }

        public SimulationDiverged(int At) : base("Simulation diverged.") { at = At; }
    }

    /// <summary>
    /// Simulate a circuit.
    /// </summary>
    public class Simulation
    {
        protected static readonly Variable t = TransientSolution.t;

        // Largest delay we expect to see. BDF6 is the largest possible
        // realistic (or theoretically possible?) method.
        protected const int MaxDelay = -6;

        private long n = 0;
        /// <summary>
        /// Get which sample the simulation is at.
        /// </summary>
        public long At { get { return n; } }
        /// <summary>
        /// Get the simulation time.
        /// </summary>
        public double Time { get { return At * TimeStep; } }

        /// <summary>
        /// Get the timestep for the simulation.
        /// </summary>
        public double TimeStep { get { return (double)(Solution.TimeStep * oversample); } }

        private ILog log = new NullLog();
        /// <summary>
        /// Log associated with this simulation.
        /// </summary>
        public ILog Log { get { return log; } set { log = value; } }

        private TransientSolution solution;
        /// <summary>
        /// Solution of the circuit we are simulating.
        /// </summary>
        public TransientSolution Solution
        {
            get { return solution; }
            set { solution = value; InvalidateProcess(); }
        }

        private int oversample = 8;
        /// <summary>
        /// Oversampling factor for this simulation.
        /// </summary>
        public int Oversample { get { return oversample; } set { oversample = value; InvalidateProcess(); } }

        /// <summary>Newton-solver diagnostics (unconverged-solve counts, output magnitude, optional trace).</summary>
        public NewtonStats Stats { get; } = new NewtonStats();

        private double magnitudeLimit = 1e6;
        /// <summary>
        /// A finite output magnitude (volts) beyond which the render is treated as diverged. The existing check only
        /// catches NaN/infinity, so a numerical blow-up to e.g. 5e37 V passed silently. 0 disables the check.
        /// </summary>
        public double MagnitudeLimit { get { return magnitudeLimit; } set { magnitudeLimit = value; InvalidateProcess(); } }

        private int traceNewton = 0;
        /// <summary>Keep the per-iteration trajectories of the first N unconverged solves (0 = off; slows the solve).</summary>
        public int TraceNewton { get { return traceNewton; } set { traceNewton = value; InvalidateProcess(); } }

        private int lineSearchAfter = 20;
        /// <summary>Backtracking only engages after this many plain Newton iterations, so solves that converge quickly are untouched.</summary>
        public int LineSearchAfter { get { return lineSearchAfter; } set { lineSearchAfter = value; InvalidateProcess(); } }

        private int lineSearch = 0;
        /// <summary>
        /// Default off. If &gt; 0: residual-monitored backtracking for solves that are not converging. After each Newton
        /// update the next iteration's assembly yields ||F|| at the new point for free; once LineSearchAfter plain
        /// iterations have passed, a step after which the squared residual norm grew by more than LineSearchSlack
        /// (relative) is undone and a halved step taken instead, up to this many halvings per step. Solves that
        /// converge within LineSearchAfter iterations are unaffected (bit-identical).
        /// </summary>
        public int LineSearch { get { return lineSearch; } set { lineSearch = value; InvalidateProcess(); } }

        private double lineSearchSlack = 0.1;
        /// <summary>Relative growth of the squared residual norm tolerated before a step is backtracked.</summary>
        public double LineSearchSlack { get { return lineSearchSlack; } set { lineSearchSlack = value; InvalidateProcess(); } }

        private double trustRegion = 0.0;
        /// <summary>
        /// EXPERIMENTAL, default off. If &gt; 0, scale the whole Newton step vector when its norm exceeds this many
        /// volts (direction-preserving, as in hotspice). The right radius depends on the circuit's voltage scale.
        /// </summary>
        public double TrustRegion { get { return trustRegion; } set { trustRegion = value; InvalidateProcess(); } }

        private int iterations = 8;
        /// <summary>
        /// Maximum number of iterations allowed for the simulation to converge.
        /// </summary>
        public int Iterations { get { return iterations; } set { iterations = value; InvalidateProcess(); } }

        /// <summary>
        /// The sampling rate of this simulation, the sampling rate of the transient solution divided by the oversampling factor.
        /// </summary>
        public Expression SampleRate { get { return 1 / (Solution.TimeStep * oversample); } }

        private Expression[] input = new Expression[] { };
        /// <summary>
        /// Expressions representing input samples.
        /// </summary>
        public IEnumerable<Expression> Input { get { return input; } set { input = value.ToArray(); InvalidateProcess(); } }

        private Expression[] output = new Expression[] { };
        /// <summary>
        /// Expressions for output samples.
        /// </summary>
        public IEnumerable<Expression> Output { get { return output; } set { output = value.ToArray(); InvalidateProcess(); } }

        // Stores any global state in the simulation (previous state values, mostly).
        private Dictionary<Expression, GlobalExpr<double>> globals = new Dictionary<Expression, GlobalExpr<double>>();
        // Add a new global and set it to 0 if it didn't already exist.
        private void AddGlobal(Expression Name)
        {
            if (!globals.ContainsKey(Name))
                globals.Add(Name, new GlobalExpr<double>(0.0));
        }

        /// <summary>
        /// Create a simulation using the given solution and the specified inputs/outputs.
        /// </summary>
        /// <param name="Solution">Transient solution to run.</param>
        /// <param name="Input">Expressions in the solution to be defined by input samples.</param>
        /// <param name="Output">Expressions describing outputs to be saved from the simulation.</param>
        public Simulation(TransientSolution Solution)
        {
            solution = Solution;

            // If any system depends on the previous value of an unknown, we need a global variable for it.
            for (int n = -1; n >= MaxDelay; n--)
            {
                Arrow t_tn = Arrow.New(t, t + n * Solution.TimeStep);
                IEnumerable<Expression> unknowns_tn = Solution.Solutions.SelectMany(i => i.Unknowns).Select(i => i.Evaluate(t_tn));
                if (!Solution.Solutions.Any(i => i.DependsOn(unknowns_tn)))
                    break;
                
                foreach (Expression i in Solution.Solutions.SelectMany(i => i.Unknowns))
                    AddGlobal(i.Evaluate(t_tn));
            }

            // Also need globals for any Newton's method unknowns.
            Arrow t_t1 = Arrow.New(t, t - Solution.TimeStep);
            foreach (Expression i in Solution.Solutions.OfType<NewtonIteration>().SelectMany(i => i.Unknowns))
                AddGlobal(i.Evaluate(t_t1));

            // Set the global values to the initial conditions of the solution.
            foreach (KeyValuePair<Expression, GlobalExpr<double>> i in globals)
            {
                // Dumb hack to get f[t - x] -> f[0] for any x.
                Expression i_t0 = i.Key.Evaluate(t, Real.Infinity).Substitute(Real.Infinity, 0);
                Expression init = i_t0.Evaluate(Solution.InitialConditions);
                i.Value.Value = init is Constant ? (double)init : 0.0;
            }

            InvalidateProcess();
        }

        /// <summary>
        /// Process some samples with this simulation. The Input and Output buffers must match the enumerations provided
        /// at initialization.
        /// </summary>
        /// <param name="N">Number of samples to process.</param>
        /// <param name="Input">Buffers that describe the input samples.</param>
        /// <param name="Output">Buffers to receive output samples.</param>
        public void Run(int N, IEnumerable<double[]> Input, IEnumerable<double[]> Output)
        {
            if (_process == null)
                _process = DefineProcess();

            try
            {
                try
                {
                    _process(N, n*TimeStep, Input.AsArray(), Output.AsArray());
                    n += N;
                }
                catch (TargetInvocationException Ex)
                {
                    throw Ex.InnerException;
                }
            }
            catch (SimulationDiverged Ex)
            {
                throw new SimulationDiverged("Simulation diverged near t = " + Quantity.ToString(Time, Units.s) + " + " + Ex.At, n + Ex.At);
            }
        }
        public void Run(int N, IEnumerable<double[]> Output) { Run(N, new double[][] { }, Output); }
        public void Run(double[] Input, IEnumerable<double[]> Output) { Run(Input.Length, new[] { Input }, Output); }
        public void Run(double[] Input, double[] Output) { Run(Input.Length, new[] { Input }, new[] { Output }); }

        private Action<int, double, double[][], double[][]> _process;
        // Force rebuilding of the process function.
        private void InvalidateProcess()
        {
            _process = null;
        }

        // The resulting lambda processes N samples, using buffers provided for Input and Output:
        //  void Process(int N, double t0, double T, double[] Input0 ..., double[] Output0 ...)
        //  { ... }
        private Action<int, double, double[][], double[][]> DefineProcess()
        {
            // Map expressions to identifiers in the syntax tree.
            var inputs = new List<KeyValuePair<Expression, LinqExpr>>();
            var outputs = new List<KeyValuePair<Expression, LinqExpr>>();

            // Lambda code generator.
            CodeGen code = new CodeGen();

            // Create parameters for the basic simulation info (N, t, Iterations).
            ParamExpr SampleCount = code.Decl<int>(Scope.Parameter, "SampleCount");
            ParamExpr t = code.Decl(Scope.Parameter, Simulation.t);
            var ins = code.Decl<double[][]>(Scope.Parameter, "ins");
            var outs = code.Decl<double[][]>(Scope.Parameter, "outs");

            // Create buffer parameters for each input...
            for (int i = 0; i < input.Length; i++)
            {
                inputs.Add(new KeyValuePair<Expression, LinqExpr>(input[i], LinqExpr.ArrayAccess(ins, LinqExpr.Constant(i))));
            }

            // ... and output.
            for (int i = 0; i < output.Length; i++)
            {
                outputs.Add(new KeyValuePair<Expression, LinqExpr>(output[i], LinqExpr.ArrayAccess(outs, LinqExpr.Constant(i))));
            }

            Arrow t_t1 = Arrow.New(Simulation.t, Simulation.t - Solution.TimeStep);

            // Create globals to store previous values of inputs.
            foreach (Expression i in Input.Distinct())
                AddGlobal(i.Evaluate(t_t1));

            // Define lambda body.

            // int Zero = 0
            LinqExpr Zero = LinqExpr.Constant(0);

            // double h = T / Oversample
            LinqExpr h = LinqExpr.Constant(TimeStep / (double)Oversample);

            // double invOversample = 1 / Oversample
            LinqExpr invOversample = LinqExpr.Constant(1.0 / (double)Oversample);

            // Load the globals to local variables and add them to the map.
            foreach (KeyValuePair<Expression, GlobalExpr<double>> i in globals)
                code.DeclInit(i.Key, i.Value);

            foreach (KeyValuePair<Expression, LinqExpr> i in inputs)
                code.DeclInit(i.Key, code[i.Key.Evaluate(t_t1)]);

            // Create arrays for linear systems.
            int M = Solution.Solutions.OfType<NewtonIteration>().Max(i => i.Equations.Count(), 0);
            int N = Solution.Solutions.OfType<NewtonIteration>().Max(i => i.UnknownDeltas.Count(), 0);
            // If there is an underdetermined system of equations, avoid out of bounds reads.
            M = Math.Max(M, N);
            // Add a column for the solution vector.
            ++N;
            Log.WriteLine(MessageType.Verbose, Vector.IsHardwareAccelerated ? "Vector hardware acceleration enabled" : "No vector hardware acceleration");

            LinqExpr JxF = code.DeclInit<double[][]>("JxF", LinqExpr.NewArrayBounds(typeof(double[]), LinqExpr.Constant(M)));
            for (int j = 0; j < M; ++j)
                code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(JxF, LinqExpr.Constant(j)), LinqExpr.NewArrayBounds(typeof(double), Vector.IsHardwareAccelerated ? LinqExpr.Constant(N + Vector<double>.Count - 1) : LinqExpr.Constant(N))));

            // Newton diagnostics: always-on counters kept in locals, flushed to Stats once per chunk and on divergence.
            LinqExpr statsConst = LinqExpr.Constant(Stats);
            ParamExpr nwBase = code.DeclInit<long>("nwBase", LinqExpr.Field(statsConst, nameof(NewtonStats.SamplesProcessed)));
            ParamExpr nwSolves = code.DeclInit<long>("nwSolves", 0L);
            ParamExpr nwExh = code.DeclInit<long>("nwExh", 0L);
            ParamExpr nwSevere = code.DeclInit<long>("nwSevere", 0L);
            ParamExpr nwFirst = code.DeclInit<long>("nwFirst", -1L);
            ParamExpr nwLast = code.DeclInit<long>("nwLast", -1L);
            ParamExpr nwMax = code.DeclInit<double>("nwMax", 0.0);
            Func<LinqExpr> flushStats = () => LinqExpr.Call(statsConst, typeof(NewtonStats).GetMethod(nameof(NewtonStats.Add)),
                nwSolves, nwExh, nwSevere, nwFirst, nwLast, nwMax, SampleCount);

            // for (int n = 0; n < SampleCount; ++n)
            ParamExpr n = code.Decl<int>("n");
            code.For(
                () => code.Add(LinqExpr.Assign(n, Zero)),
                LinqExpr.LessThan(n, SampleCount),
                () => code.Add(LinqExpr.PreIncrementAssign(n)),
                () =>
                {
                    // Prepare input samples for oversampling interpolation.
                    Dictionary<Expression, LinqExpr> dVi = new Dictionary<Expression, LinqExpr>();
                    foreach (Expression i in Input.Distinct())
                    {
                        LinqExpr Va = code[i];
                        // Sum all inputs with this key.
                        IEnumerable<LinqExpr> Vbs = inputs.Where(j => j.Key.Equals(i)).Select(j => j.Value);
                        LinqExpr Vb = LinqExpr.ArrayAccess(Vbs.First(), n);
                        foreach (LinqExpr j in Vbs.Skip(1))
                            Vb = LinqExpr.Add(Vb, LinqExpr.ArrayAccess(j, n));

                        // dVi = (Vb - Va) / Oversample
                        code.Add(LinqExpr.Assign(
                            Decl<double>(code, dVi, i, "d" + i.ToString().Replace("[t]", "")),
                            LinqExpr.Multiply(LinqExpr.Subtract(Vb, Va), invOversample)));
                    }

                    // Prepare output sample accumulators for low pass filtering.
                    Dictionary<Expression, LinqExpr> Vo = new Dictionary<Expression, LinqExpr>();
                    foreach (Expression i in Output.Distinct())
                        code.Add(LinqExpr.Assign(
                            Decl<double>(code, Vo, i, i.ToString().Replace("[t]", "")),
                            LinqExpr.Constant(0.0)));

                    // int ov = Oversample; 
                    // do { -- ov; } while(ov > 0)
                    ParamExpr ov = code.Decl<int>("ov");
                    code.Add(LinqExpr.Assign(ov, LinqExpr.Constant(Oversample)));
                    code.DoWhile(() =>
                    {
                        // t += h
                        code.Add(LinqExpr.AddAssign(t, h));

                        // Interpolate the input samples.
                        foreach (Expression i in Input.Distinct())
                            code.Add(LinqExpr.AddAssign(code[i], dVi[i]));

                        // Compile all of the SolutionSets in the solution.
                        foreach (SolutionSet ss in Solution.Solutions)
                        {
                            if (ss is LinearSolutions)
                            {
                                // Linear solutions are easy.
                                LinearSolutions S = (LinearSolutions)ss;
                                foreach (Arrow i in S.Solutions)
                                    code.DeclInit(i.Left, i.Right);
                            }
                            else if (ss is NewtonIteration)
                            {
                                NewtonIteration S = (NewtonIteration)ss;

                                // Start with the initial guesses from the solution.
                                foreach (Arrow i in S.Guesses)
                                    code.DeclInit(i.Left, i.Right);

                                // int it = iterations
                                LinqExpr it = code.ReDeclInit<int>("it", Iterations);
                                // Largest Newton step of the most recent iteration, to tell a failed solve from harmless chatter.
                                LinqExpr nwLastStep = code.ReDeclInit<double>("nwlaststep", 0.0);
                                // EXPERIMENTAL residual-monitored backtracking: state that persists across iterations.
                                LinqExpr lsFnow = null, lsFprev = null, lsAlpha = null, lsBack = null;
                                var lsVo = new Dictionary<Expression, LinqExpr>();
                                var lsDp = new Dictionary<Expression, LinqExpr>();
                                System.Linq.Expressions.LabelTarget lsSkip = null;
                                if (LineSearch > 0)
                                {
                                    lsFnow = code.ReDeclInit<double>("lsfnow", 0.0);
                                    lsFprev = code.ReDeclInit<double>("lsfprev", 1e300);
                                    lsAlpha = code.ReDeclInit<double>("lsalpha", 1.0);
                                    lsBack = code.ReDeclInit<int>("lsback", 0);
                                    int lk = 0;
                                    foreach (Expression u in S.Unknowns)
                                    {
                                        lsVo[u] = code.ReDeclInit<double>("lsvo" + lk, 0.0);
                                        lsDp[u] = code.ReDeclInit<double>("lsdp" + lk, 0.0);
                                        lk++;
                                    }
                                    lsSkip = LinqExpr.Label("ls_skip");
                                }
                                // do { ... --it } while(it > 0)
                                code.DoWhile((Break) =>
                                {
                                    // Solve the un-solved system.
                                    Solve(code, JxF, S.Equations, S.UnknownDeltas, lsFnow);

                                    // Optional trust region: scale the whole step if its norm exceeds TrustRegion volts.
                                    if (TrustRegion > 0)
                                    {
                                        LinqExpr trsn = code.ReDeclInit<double>("trsn", 0.0);
                                        foreach (Expression d in S.UnknownDeltas)
                                            code.Add(LinqExpr.AddAssign(trsn, LinqExpr.Multiply(code[d], code[d])));
                                        LinqExpr trsc = code.ReDeclInit<double>("trsc", 1.0);
                                        code.Add(LinqExpr.IfThen(LinqExpr.GreaterThan(trsn, LinqExpr.Constant(TrustRegion * TrustRegion)),
                                            LinqExpr.Assign(trsc, LinqExpr.Divide(LinqExpr.Constant(TrustRegion),
                                                LinqExpr.Call(typeof(Math).GetMethod("Sqrt", new Type[] { typeof(double) }), trsn)))));
                                        foreach (Expression d in S.UnknownDeltas)
                                            code.Add(LinqExpr.MultiplyAssign(code[d], trsc));
                                    }

                                    // Compile the pre-solved solutions.
                                    if (S.KnownDeltas != null)
                                        foreach (Arrow i in S.KnownDeltas)
                                            code.DeclInit(i.Left, i.Right);

                                    // EXPERIMENTAL residual-monitored backtracking.
                                    if (LineSearch > 0)
                                    {
                                        // Is the Newton step at this point already below the step tolerance? Then never backtrack.
                                        LinqExpr lsConv = code.ReDeclInit("lsconv", true);
                                        foreach (Expression u in S.Unknowns)
                                            code.Add(LinqExpr.AndAssign(lsConv, LinqExpr.LessThan(Abs(code[NewtonIteration.Delta(u)]),
                                                MultiplyAdd(Abs(code[u]), LinqExpr.Constant(1e-4), LinqExpr.Constant(1e-6)))));
                                        // Bad: we have a base point, the residual at THIS point grew clearly, and halvings remain.
                                        LinqExpr lsBad = LinqExpr.AndAlso(
                                            LinqExpr.AndAlso(LinqExpr.AndAlso(LinqExpr.LessThan(lsFprev, LinqExpr.Constant(1e299)), LinqExpr.Not(lsConv)),
                                                LinqExpr.GreaterThanOrEqual(LinqExpr.Subtract(LinqExpr.Constant(Iterations), it), LinqExpr.Constant(LineSearchAfter))),
                                            LinqExpr.AndAlso(LinqExpr.LessThan(lsBack, LinqExpr.Constant(LineSearch)),
                                                LinqExpr.GreaterThan(lsFnow, LinqExpr.Multiply(LinqExpr.Constant(1.0 + LineSearchSlack), lsFprev))));
                                        var lsRevert = new List<LinqExpr>();
                                        lsRevert.Add(LinqExpr.PreIncrementAssign(lsBack));
                                        lsRevert.Add(LinqExpr.Assign(lsAlpha, LinqExpr.Multiply(lsAlpha, LinqExpr.Constant(0.5))));
                                        foreach (Expression u in S.Unknowns)
                                            lsRevert.Add(LinqExpr.Assign(code[u], LinqExpr.Add(lsVo[u], LinqExpr.Multiply(lsAlpha, lsDp[u]))));
                                        lsRevert.Add(LinqExpr.Goto(lsSkip));
                                        code.Add(LinqExpr.IfThen(lsBad, LinqExpr.Block(lsRevert)));
                                        // Accepted: this point becomes the new base, and its full step is remembered.
                                        foreach (Expression u in S.Unknowns)
                                        {
                                            code.Add(LinqExpr.Assign(lsVo[u], code[u]));
                                            code.Add(LinqExpr.Assign(lsDp[u], code[NewtonIteration.Delta(u)]));
                                        }
                                        code.Add(LinqExpr.Assign(lsAlpha, LinqExpr.Constant(1.0)));
                                        code.Add(LinqExpr.Assign(lsBack, LinqExpr.Constant(0)));
                                        code.Add(LinqExpr.Assign(lsFprev, lsFnow));
                                    }

                                    // Remember the size of this iteration's step (cheap: one compare per unknown).
                                    code.Add(LinqExpr.Assign(nwLastStep, LinqExpr.Constant(0.0)));
                                    foreach (Expression u in S.Unknowns)
                                    {
                                        LinqExpr nwa = Abs(code[NewtonIteration.Delta(u)]);
                                        code.Add(LinqExpr.IfThen(LinqExpr.GreaterThan(nwa, nwLastStep), LinqExpr.Assign(nwLastStep, nwa)));
                                    }

                                    // Opt-in per-iteration trace: largest step, largest unknown, and the dominant unknown.
                                    if (TraceNewton > 0)
                                    {
                                        LinqExpr tdvm = code.ReDeclInit<double>("tdvm", 0.0);
                                        LinqExpr tvm = code.ReDeclInit<double>("tvm", 0.0);
                                        LinqExpr targ = code.ReDeclInit<int>("targ", 0);
                                        LinqExpr tdva = code.ReDeclInit<double>("tdva", 0.0);
                                        LinqExpr tva = code.ReDeclInit<double>("tva", 0.0);
                                        int tk = 0;
                                        foreach (Expression ui in S.Unknowns)
                                        {
                                            LinqExpr tv0 = code[ui];
                                            LinqExpr tdv0 = code[NewtonIteration.Delta(ui)];
                                            code.Add(LinqExpr.IfThen(LinqExpr.GreaterThan(Abs(tdv0), tdvm), LinqExpr.Block(
                                                LinqExpr.Assign(tdvm, Abs(tdv0)), LinqExpr.Assign(targ, LinqExpr.Constant(tk)),
                                                LinqExpr.Assign(tdva, tdv0), LinqExpr.Assign(tva, tv0))));
                                            code.Add(LinqExpr.IfThen(LinqExpr.GreaterThan(Abs(tv0), tvm), LinqExpr.Assign(tvm, Abs(tv0))));
                                            tk++;
                                        }
                                        Stats.UnknownNames = S.Unknowns.Select(u => u.ToString()).ToArray();
                                        Stats.TraceLimit = TraceNewton;
                                        code.Add(LinqExpr.Call(statsConst, typeof(NewtonStats).GetMethod(nameof(NewtonStats.TraceIter)),
                                            LinqExpr.Add(LinqExpr.Subtract(LinqExpr.Constant(Iterations), it), LinqExpr.Constant(1)),
                                            tdvm, tvm, targ, tdva, tva));
                                    }

                                    // bool done = true
                                    LinqExpr done = code.ReDeclInit("done", true);
                                    foreach (Expression i in S.Unknowns)
                                    {
                                        LinqExpr v = code[i];
                                        LinqExpr dv = code[NewtonIteration.Delta(i)];

                                        // done &= (|dv| < |v|*epsilon)
                                        code.Add(LinqExpr.AndAssign(done, LinqExpr.LessThan(Abs(dv), MultiplyAdd(Abs(v), LinqExpr.Constant(1e-4), LinqExpr.Constant(1e-6)))));
                                        // v += dv
                                        code.Add(LinqExpr.AddAssign(v, dv));
                                    }
                                    // if (done) break
                                    code.Add(LinqExpr.IfThen(done, Break));

                                    // A backtracked iteration jumps here: it counts against the cap but applies no update.
                                    if (LineSearch > 0)
                                        code.Add(LinqExpr.Label(lsSkip));
                                    // --it;
                                    code.Add(LinqExpr.PreDecrementAssign(it));
                                }, LinqExpr.GreaterThan(it, Zero));

                                // Newton diagnostics: count this solve. it == 0 only if the cap ran out without converging.
                                LinqExpr nwPos = LinqExpr.Add(nwBase, LinqExpr.Convert(n, typeof(long)));
                                code.Add(LinqExpr.PreIncrementAssign(nwSolves));
                                code.Add(LinqExpr.IfThen(LinqExpr.Equal(it, Zero), LinqExpr.Block(
                                    LinqExpr.PreIncrementAssign(nwExh),
                                    // Severe: the cap ran out while the step was still larger than 1 (V or A), not just chattering at a rounding floor.
                                    LinqExpr.IfThen(LinqExpr.GreaterThan(nwLastStep, LinqExpr.Constant(1.0)), LinqExpr.PreIncrementAssign(nwSevere)),
                                    LinqExpr.IfThen(LinqExpr.LessThan(nwFirst, LinqExpr.Constant(0L)), LinqExpr.Assign(nwFirst, nwPos)),
                                    LinqExpr.Assign(nwLast, nwPos))));
                                if (TraceNewton > 0)
                                    code.Add(LinqExpr.Call(statsConst, typeof(NewtonStats).GetMethod(nameof(NewtonStats.EndSolve)), nwPos,
                                        LinqExpr.Condition(LinqExpr.Equal(it, Zero), LinqExpr.Constant(1), LinqExpr.Constant(0))));

                                //// bool failed = false
                                //LinqExpr failed = Decl(code, code, "failed", LinqExpr.Constant(false));
                                //for (int i = 0; i < eqs.Length; ++i)
                                //    // failed |= |JxFi| > epsilon
                                //    code.Add(LinqExpr.OrAssign(failed, LinqExpr.GreaterThan(Abs(eqs[i].ToExpression().Compile(map)), LinqExpr.Constant(1e-3))));

                                //code.Add(LinqExpr.IfThen(failed, ThrowSimulationDiverged(n)));
                            }
                        }

                        // Update the previous timestep variables.
                        foreach (SolutionSet S in Solution.Solutions)
                        {
                            for (int m = MaxDelay; m < 0; m++)
                            {
                                Arrow t_tm = Arrow.New(Simulation.t, Simulation.t + m * Solution.TimeStep);
                                Arrow t_tm1 = Arrow.New(Simulation.t, Simulation.t + (m + 1) * Solution.TimeStep);
                                foreach (Expression i in S.Unknowns.Where(i => globals.Keys.Contains(i.Evaluate(t_tm))))
                                    code.Add(LinqExpr.Assign(code[i.Evaluate(t_tm)], code[i.Evaluate(t_tm1)]));
                            }
                        }

                        // Vo += i
                        foreach (Expression i in Output.Distinct())
                        {
                            LinqExpr Voi = LinqExpr.Constant(0.0);
                            try
                            {
                                Voi = code.Compile(i);
                            }
                            catch (Exception Ex)
                            {
                                Log.WriteLine(MessageType.Warning, Ex.Message);
                            }
                            code.Add(LinqExpr.AddAssign(Vo[i], Voi));
                        }

                        // Vi_t0 = Vi
                        foreach (Expression i in Input.Distinct())
                            code.Add(LinqExpr.Assign(code[i.Evaluate(t_t1)], code[i]));

                        // --ov;
                        code.Add(LinqExpr.PreDecrementAssign(ov));
                    }, LinqExpr.GreaterThan(ov, Zero));

                    // Output[i][n] = Vo / Oversample
                    foreach (KeyValuePair<Expression, LinqExpr> i in outputs)
                        code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(i.Value, n), LinqExpr.Multiply(Vo[i.Key], invOversample)));

                    // Newton diagnostics: track the largest output magnitude.
                    foreach (KeyValuePair<Expression, LinqExpr> i in Vo)
                    {
                        LinqExpr mag = LinqExpr.Multiply(Abs(i.Value), invOversample);
                        code.Add(LinqExpr.IfThen(LinqExpr.GreaterThan(mag, nwMax), LinqExpr.Assign(nwMax, mag)));
                    }

                    // Every 256 samples, check for divergence.
                    if (Vo.Any())
                        code.Add(LinqExpr.IfThen(LinqExpr.Equal(LinqExpr.And(n, LinqExpr.Constant(0xFF)), Zero),
                            LinqExpr.Block(Vo.Select(i => LinqExpr.IfThenElse(
                                MagnitudeLimit > 0
                                    ? LinqExpr.OrElse(IsNotReal(i.Value), LinqExpr.GreaterThan(Abs(i.Value), LinqExpr.Constant(MagnitudeLimit * Oversample)))
                                    : IsNotReal(i.Value),
                                LinqExpr.Block(flushStats(), ThrowSimulationDiverged(n)),
                                LinqExpr.Assign(i.Value, RoundDenormToZero(i.Value)))))));
                });

            // Newton diagnostics: flush the counters for this chunk.
            code.Add(flushStats());

            // Copy the global state variables back to the globals.
            foreach (KeyValuePair<Expression, GlobalExpr<double>> i in globals)
                code.Add(LinqExpr.Assign(i.Value, code[i.Key]));

            var lambda = code.Build<Action<int, double, double[][], double[][]>>();
            return lambda.Compile();
        }

        // Solve a system of linear equations
        private static void Solve(CodeGen code, LinqExpr Ab, IEnumerable<LinearCombination> Equations, IEnumerable<Expression> Unknowns, LinqExpr fOut = null)
        {
            LinearCombination[] eqs = Equations.ToArray();
            Expression[] deltas = Unknowns.ToArray();

            int M = eqs.Length;
            int N = deltas.Length;

            // Initialize the matrix.
            for (int i = 0; i < M; ++i)
            {
                LinqExpr Abi = code.ReDeclInit<double[]>("Abi", LinqExpr.ArrayAccess(Ab, LinqExpr.Constant(i)));
                for (int x = 0; x < N; ++x)
                    code.Add(LinqExpr.Assign(
                        LinqExpr.ArrayAccess(Abi, LinqExpr.Constant(x)),
                        code.Compile(eqs[i][deltas[x]])));
                code.Add(LinqExpr.Assign(
                    LinqExpr.ArrayAccess(Abi, LinqExpr.Constant(N)),
                    code.Compile(eqs[i][1])));
            }
            // In case we have fewer equations than unknowns, we can avoid dumb failures to converge by just
            // avoiding "uninitialized" memory left over in the buffer from previous solutions.
            for (int i = M; i < N; ++i)
            {
                LinqExpr Abi = code.ReDeclInit<double[]>("Abi", LinqExpr.ArrayAccess(Ab, LinqExpr.Constant(i)));
                code.Add(LinqExpr.Assign(
                    LinqExpr.ArrayAccess(Abi, LinqExpr.Constant(N)), 
                    LinqExpr.Constant(0.0)));
            }

            // Optional: the squared residual norm at the current point is the right-hand-side column BEFORE elimination.
            if (fOut != null)
            {
                LinqExpr fs = LinqExpr.Constant(0.0);
                for (int i = 0; i < M; ++i)
                {
                    LinqExpr fi = LinqExpr.ArrayAccess(LinqExpr.ArrayAccess(Ab, LinqExpr.Constant(i)), LinqExpr.Constant(N));
                    fs = LinqExpr.Add(fs, LinqExpr.Multiply(fi, fi));
                }
                code.Add(LinqExpr.Assign(fOut, fs));
            }

            // Fully solve this system of equations.
            code.Add(LinqExpr.Call(
                GetMethod<Simulation>(Vector.IsHardwareAccelerated ? nameof(SolveVector) : nameof(Solve), Ab.Type, typeof(int), typeof(int)),
                Ab,
                LinqExpr.Constant(M),
                LinqExpr.Constant(N + 1)));

            // Extract the solutions.
            for (int j = 0; j < N; ++j)
                code.DeclInit(deltas[j], LinqExpr.Negate(LinqExpr.ArrayAccess(LinqExpr.ArrayAccess(Ab, LinqExpr.Constant(j)), LinqExpr.Constant(N))));
        }

        // A human readable implementation of RowReduce.
        public static void Solve(double[][] Ab, int M, int N)
        {
            // Solve for dx.
            // For each column...
            for (int j = 0; j < Math.Min(M, N); ++j)
            {
                int pi = j;
                double max = Math.Abs(Ab[j][j]);

                // Find a pivot row for this variable.
                for (int i = j + 1; i < M; ++i)
                {
                    double[] Abi = Ab[i];
                    // if(|JxF[i][j]| > max) { pi = i, max = |JxF[i][j]| }
                    double maxj = Math.Abs(Abi[j]);
                    if (maxj > max)
                    {
                        pi = i;
                        max = maxj;
                    }
                }

                // Swap pivot row with the current row.
                if (pi != j)
                {
                    var Abpi = Ab[pi];
                    Ab[pi] = Ab[j];
                    Ab[j] = Abpi;
                }

                double[] Abj = Ab[j];

                // Eliminate all other rows.
                double p = Abj[j];
                if (p == 0) continue;
                for (int i = 0; i < M; ++i)
                {
                    if (i == j) continue;
                    double[] Abi = Ab[i];
                    if (Abi[j] == 0.0) continue;

                    double s = Abi[j] / p;
                    for (int ij = j + 1; ij < N; ++ij)
                        Abi[ij] -= Abj[ij] * s;
                }

                // Scale the pivot row, so the pivot is one.
                double inv_p = 1.0 / p;
                for (int ij = j + 1; ij < N; ++ij)
                    Abj[ij] *= inv_p;
            }
        }

        //This algorith has no tail-loop - it requires arrays to be padded to N + Vector.Count - 1
        private static void SolveVector(double[][] Ab, int M, int N)
        {
            var vectorLength = Vector<double>.Count;

            // Solve for dx.
            // For each variable in the system...
            for (int j = 0; j < Math.Min(M, N); ++j)
            {
                int pi = j;
                double max = Math.Abs(Ab[j][j]);

                // Find a pivot row for this variable.
                for (int i = j + 1; i < M; ++i)
                {
                    // if(|JxF[i][j]| > max) { pi = i, max = |JxF[i][j]| }
                    double maxj = Math.Abs(Ab[i][j]);
                    if (maxj > max)
                    {
                        pi = i;
                        max = maxj;
                    }
                }

                // Swap pivot row with the current row.
                if (pi != j)
                {
                    var tmp = Ab[pi];
                    Ab[pi] = Ab[j];
                    Ab[j] = tmp;
                }

                double[] Abj = Ab[j];

                // Eliminate all other rows.
                double p = Abj[j];
                if (p == 0) continue;
                for (int i = 0; i < M; ++i)
                {
                    if (i == j) continue;
                    double[] Abi = Ab[i];
                    if (Abi[j] == 0) continue;

                    double s = Abi[j] / p;
                    for (int ij = j + 1; ij < N; ij += vectorLength)
                    {
                        var source = new Vector<double>(Abj, ij);
                        var target = new Vector<double>(Abi, ij);
                        var res = target - (source * s);
                        res.CopyTo(Abi, ij);
                    }
                }

                // Scale the pivot row, so the pivot is one.
                // TODO: Vectorize
                double inv_p = 1.0 / p;
                for (int ij = j + 1; ij < N; ++ij)
                    Abj[ij] *= inv_p;
            }
        }

        // Returns a throw SimulationDiverged expression at At.
        private LinqExpr ThrowSimulationDiverged(LinqExpr At)
        {
            return LinqExpr.Throw(LinqExpr.New(typeof(SimulationDiverged).GetConstructor(new Type[] { At.Type }), At));
        }

        private static ParamExpr Decl<T>(CodeGen Target, ICollection<KeyValuePair<Expression, LinqExpr>> Map, Expression Expr, string Name)
        {
            ParamExpr p = Target.Decl<T>(Name);
            Map.Add(new KeyValuePair<Expression, LinqExpr>(Expr, p));
            return p;
        }

        private static ParamExpr Decl<T>(CodeGen Target, ICollection<KeyValuePair<Expression, LinqExpr>> Map, Expression Expr)
        {
            return Decl<T>(Target, Map, Expr, Expr.ToString());
        }

        private static LinqExpr ConstantExpr(double x, Type T)
        {
            if (T == typeof(double))
                return LinqExpr.Constant(x);
            else if (T == typeof(float))
                return LinqExpr.Constant((float)x);
            else
                throw new NotImplementedException("Constant");
        }

        // Get a method of T with the given name/param types.
        private static MethodInfo GetMethod(Type T, string Name, params Type[] ParamTypes) { return T.GetMethod(Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, ParamTypes, null); }
        private static MethodInfo GetMethod<T>(string Name, params Type[] ParamTypes) { return GetMethod(typeof(T), Name, ParamTypes); }

        // Returns a * b + c.
        private static LinqExpr MultiplyAdd(LinqExpr a, LinqExpr b, LinqExpr c) { return LinqExpr.Add(LinqExpr.Multiply(a, b), c); }
        // Returns 1 / x.
        private static LinqExpr Reciprocal(LinqExpr x) { return LinqExpr.Divide(ConstantExpr(1.0, x.Type), x); }
        // Returns abs(x).
        private static LinqExpr Abs(LinqExpr x) { return LinqExpr.Call(GetMethod(typeof(Math), "Abs", x.Type), x); }
        // Returns x*x.
        private static LinqExpr Square(LinqExpr x) { return LinqExpr.Multiply(x, x); }

        // Returns true if x is not NaN or Inf
        private static LinqExpr IsNotReal(LinqExpr x)
        {
            return LinqExpr.Or(
                LinqExpr.Call(GetMethod(x.Type, "IsNaN", x.Type), x),
                LinqExpr.Call(GetMethod(x.Type, "IsInfinity", x.Type), x));
        }
        // Round x to zero if it is sub-normal.
        private static LinqExpr RoundDenormToZero(LinqExpr x) { return x; }
    }
}
