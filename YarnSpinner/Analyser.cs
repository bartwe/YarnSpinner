using System;
using System.Collections.Generic;

namespace Yarn.Analysis {
    public class Diagnosis {
        public string message;
        public string nodeName;
        public int lineNumber;
        public int columnNumber;

        public enum Severity {
            Error,
            Warning,
            Note
        }

        public Severity severity;

        public Diagnosis(string message, Severity severity, string nodeName = null, int lineNumber = -1, int columnNumber = -1) {
            this.message = message;
            this.nodeName = nodeName;
            this.lineNumber = lineNumber;
            this.columnNumber = columnNumber;
            this.severity = severity;
        }

        public override string ToString() {
            return ToString(showSeverity: false);
        }

        public string ToString(bool showSeverity) {
            var contextLabel = "";

            if (showSeverity) {
                switch (severity) {
                    case Severity.Error:
                        contextLabel = "ERROR: ";
                        break;
                    case Severity.Warning:
                        contextLabel = "WARNING: ";
                        break;
                    case Severity.Note:
                        contextLabel = "Note: ";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if (nodeName != null) {
                contextLabel += nodeName;
                if (lineNumber != -1) {
                    contextLabel += string.Format(": {0}", lineNumber);

                    if (columnNumber != -1) {
                        contextLabel += string.Format(":{0}", columnNumber);
                    }
                }
            }

            string message;

            if (string.IsNullOrEmpty(contextLabel)) {
                message = this.message;
            }
            else {
                message = string.Format("{0}: {1}", contextLabel, this.message);
            }

            return message;
        }
    }

    public class Context {
        IEnumerable<Type> _defaultAnalyserClasses;

        internal IEnumerable<Type> defaultAnalyserClasses {
            get {
                var classes = new List<Type>();

                if (_defaultAnalyserClasses == null) {
                    classes = new List<Type>();

                    var assembly = GetType().Assembly;

                    foreach (var type in assembly.GetTypes()) {
                        if (type.IsSubclassOf(typeof(CompiledProgramAnalyser)) &&
                            type.IsAbstract == false) {
                            classes.Add(type);
                        }
                    }
                    _defaultAnalyserClasses = classes;
                }

                return _defaultAnalyserClasses;
            }
        }

        readonly List<CompiledProgramAnalyser> analysers;

        public Context() {
            analysers = new List<CompiledProgramAnalyser>();

            foreach (var analyserType in defaultAnalyserClasses) {
                analysers.Add((CompiledProgramAnalyser)Activator.CreateInstance(analyserType));
            }
        }

        internal void AddProgramToAnalysis(Program program) {
            foreach (var analyser in analysers) {
                analyser.Diagnose(program);
            }
        }

        public IEnumerable<Diagnosis> FinishAnalysis() {
            var diagnoses = new List<Diagnosis>();

            foreach (var analyser in analysers) {
                diagnoses.AddRange(analyser.GatherDiagnoses());
            }

            return diagnoses;
        }
    }

    abstract class ASTAnalyser {
        public abstract IEnumerable<Diagnosis> Diagnose(Parser.Node node);
    }

    abstract class CompiledProgramAnalyser {
        public abstract void Diagnose(Program program);
        public abstract IEnumerable<Diagnosis> GatherDiagnoses();
    }

    class VariableLister : CompiledProgramAnalyser {
        readonly HashSet<string> variables = new HashSet<string>();

        public override void Diagnose(Program program) {
            // In each node, find all reads and writes to variables
            foreach (var nodeInfo in program.nodes) {
                var nodeName = nodeInfo.Key;
                var theNode = nodeInfo.Value;

                foreach (var instruction in theNode.instructions) {
                    switch (instruction.operation) {
                        case ByteCode.PushVariable:
                        case ByteCode.StoreVariable:
                            variables.Add((string)instruction.operandA);
                            break;
                    }
                }
            }
        }

        public override IEnumerable<Diagnosis> GatherDiagnoses() {
            var diagnoses = new List<Diagnosis>();

            foreach (var variable in variables) {
                var d = new Diagnosis(variable, Diagnosis.Severity.Note);
                diagnoses.Add(d);
            }

            return diagnoses;
        }
    }

    class UnusedVariableChecker : CompiledProgramAnalyser {
        readonly HashSet<string> readVariables = new HashSet<string>();
        readonly HashSet<string> writtenVariables = new HashSet<string>();


        public override void Diagnose(Program program) {
            // In each node, find all reads and writes to variables
            foreach (var nodeInfo in program.nodes) {
                var nodeName = nodeInfo.Key;
                var theNode = nodeInfo.Value;

                foreach (var instruction in theNode.instructions) {
                    switch (instruction.operation) {
                        case ByteCode.PushVariable:
                            readVariables.Add((string)instruction.operandA);
                            break;
                        case ByteCode.StoreVariable:
                            writtenVariables.Add((string)instruction.operandA);
                            break;
                    }
                }
            }
        }

        public override IEnumerable<Diagnosis> GatherDiagnoses() {
            // Exclude read variables that are also written
            var readOnlyVariables = new HashSet<string>(readVariables);
            readOnlyVariables.ExceptWith(writtenVariables);

            // Exclude written variables that are also read
            var writeOnlyVariables = new HashSet<string>(writtenVariables);
            writeOnlyVariables.ExceptWith(readVariables);

            // Generate diagnoses
            var diagnoses = new List<Diagnosis>();

            foreach (var readOnlyVariable in readOnlyVariables) {
                var message = string.Format("Variable {0} is read from, but never assigned", readOnlyVariable);
                diagnoses.Add(new Diagnosis(message, Diagnosis.Severity.Warning));
            }

            foreach (var writeOnlyVariable in writeOnlyVariables) {
                var message = string.Format("Variable {0} is assigned, but never read from", writeOnlyVariable);
                diagnoses.Add(new Diagnosis(message, Diagnosis.Severity.Warning));
            }

            return diagnoses;
        }
    }
}
