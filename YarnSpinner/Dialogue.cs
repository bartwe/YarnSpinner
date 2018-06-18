/*

The MIT License (MIT)

Copyright (c) 2015 Secret Lab Pty. Ltd. and Yarn Spinner contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Yarn.Analysis;

namespace Yarn {
    // Represents things that can go wrong while loading or running
    // a dialogue.
    public class YarnException : Exception {
        public YarnException(string message) : base(message) {}
    }

    // Delegates, which are used by the client.

    // OptionChoosers let the client tell the Dialogue about what
    // response option the user selected.
    public delegate void OptionChooser(int selectedOptionIndex);

    // Loggers let the client send output to a console, for both debugging
    // and error logging.
    public delegate void Logger(string message);

    // Information about stuff that the client should handle.
    // (Currently this just wraps a single field, but doing it like this
    // gives us the option to add more stuff later without breaking the API.)
    public struct Line {
        public string text;
    }

    public struct Options {
        public IList<string> options;
    }

    public struct Command {
        public string text;
    }

    // Where we turn to for storing and loading variable data.
    public interface VariableStorage {
        [Obsolete]
        void SetNumber(string variableName, float number);

        [Obsolete]
        float GetNumber(string variableName);

        void SetValue(string variableName, Value value);
        Value GetValue(string variableName);
        void Clear();
    }

    public abstract class BaseVariableStorage : VariableStorage {
        [Obsolete]
        public void SetNumber(string variableName, float number) {
            SetValue(variableName, new Value(number));
        }

        [Obsolete]
        public float GetNumber(string variableName) {
            return GetValue(variableName).AsNumber;
        }

        public abstract void SetValue(string variableName, Value value);
        public abstract Value GetValue(string variableName);
        public abstract void Clear();
    }

    // A line, localised into the current locale.
    // LocalisedLines are used in both lines, options, and shortcut options - basically,
    // anything user-facing.
    public class LocalisedLine {
        public string LineCode { get; set; }
        public string LineText { get; set; }
        public string Comment { get; set; }
    }

    // Very simple continuity class that keeps all variables in memory
    public class MemoryVariableStore : BaseVariableStorage {
        readonly Dictionary<string, Value> variables = new Dictionary<string, Value>();

        public override void SetValue(string variableName, Value value) {
            variables[variableName] = value;
        }

        public override Value GetValue(string variableName) {
            Value value;
            if (variables.TryGetValue(variableName, out value))
                return value;
            return Value.NULL;
        }

        public override void Clear() {
            variables.Clear();
        }
    }

    // The Dialogue class is the main thing that clients will use.
    public class Dialogue {
        // We'll ask this object for the state of variables
        internal VariableStorage continuity;

        // Represents something for the end user ("client") of the Dialogue class to do.
        public abstract class RunnerResult {}

        // The client should run a line of dialogue.
        public class LineResult : RunnerResult {
            public Line line;
            public string localisationHash;

            public LineResult(string text, string hash) {
                var line = new Line();
                line.text = text;
                this.line = line;
                localisationHash = hash;
            }
        }

        // The client should run a command (it's up to them to parse the string)
        public class CommandResult : RunnerResult {
            public Command command;

            public CommandResult(string text) {
                var command = new Command();
                command.text = text;
                this.command = command;
            }
        }

        // The client should show a list of options, and call
        // setSelectedOptionDelegate before asking for the
        // next line. It's an error if you don't.
        public class OptionSetResult : RunnerResult {
            public Options options;
            public Options optionLocalisationHashes;
            public OptionChooser setSelectedOptionDelegate;

            public OptionSetResult(IList<string> optionStrings, IList<string> localisationHashes, OptionChooser setSelectedOption) {
                var options = new Options();
                options.options = optionStrings;
                this.options = options;
                optionLocalisationHashes = new Options();
                optionLocalisationHashes.options = localisationHashes;
                setSelectedOptionDelegate = setSelectedOption;
            }
        }

        // We've reached the end of this node.
        public class NodeCompleteResult : RunnerResult {
            public string nextNode;

            public NodeCompleteResult(string nextNode) {
                this.nextNode = nextNode;
            }
        }

        // Delegates used for logging.
        public Logger LogDebugMessage;
        public Logger LogErrorMessage;

        // The node we start from.
        public const string DEFAULT_START = "Start";

        // The loader contains all of the nodes we're going to run.
        public Loader loader { get; internal set; }

        // The Program is the compiled Yarn program.
        internal Program program;

        // The library contains all of the functions and operators we know about.
        public Library library;

        // The collection of nodes that we've seen.
        public Dictionary<String, int> visitedNodeCount = new Dictionary<string, int>();

        // A function exposed to Yarn that returns the number of times a node has been run.
        // If no parameters are supplied, returns the number of time the current node
        // has been run.
        object YarnFunctionNodeVisitCount(Value[] parameters) {
            // Determine the node we're checking
            string nodeName;

            if (parameters.Length == 0) {
                // No parameters? Check the current node
                nodeName = vm.currentNodeName;
            }
            else if (parameters.Length == 1) {
                // A parameter? Check the named node
                nodeName = parameters[0].AsString;

                // Ensure this node exists
                if (NodeExists(nodeName) == false) {
                    var errorMessage = string.Format("The node {0} does not " + "exist.", nodeName);
                    LogErrorMessage(errorMessage);
                    return 0;
                }
            }
            else {
                // We got too many parameters
                var errorMessage = string.Format("Incorrect number of parameters to " + "visitCount (expected 0 or 1, got {0})", parameters.Length);
                LogErrorMessage(errorMessage);
                return 0;
            }

            // Figure out how many times this node was run
            var visitCount = 0;
            visitedNodeCount.TryGetValue(nodeName, out visitCount);

            return visitCount;
        }

        // A Yarn function that returns true if the named node, or the current node 
        // if no parameters were provided, has been visited at least once.
        object YarnFunctionIsNodeVisited(Value[] parameters) {
            return (int)YarnFunctionNodeVisitCount(parameters) > 0;
        }

        public Dialogue(VariableStorage continuity) {
            this.continuity = continuity;
            loader = new Loader(this);
            library = new Library();

            library.ImportLibrary(new StandardLibrary());

            // Register the "visited" function, which returns true if we've visited
            // a node previously (nodes are marked as visited when we leave them)
            library.RegisterFunction("visited", -1, (ReturningFunction)YarnFunctionIsNodeVisited);

            // Register the "visitCount" function, which returns the number of times
            // a node has been run (which increments when a node ends). If called with 
            // no parameters, check the CURRENT node.
            library.RegisterFunction("visitCount", -1, (ReturningFunction)YarnFunctionNodeVisitCount);
        }

        // Load a file from disk.
        public void LoadFile(string fileName, bool showTokens = false, bool showParseTree = false, string onlyConsiderNode = null) {
            // Is this a compiled program file?
            if (fileName.EndsWith(".yarn.bytes")) {
                using (var stream = File.OpenRead(fileName)) {
                    var bytes = new MemoryStream();
                    var length = (int)stream.Length;
                    bytes.SetLength(length);
                    var buffer = bytes.GetBuffer();
                    var offset = (int)bytes.Seek(0, SeekOrigin.Current);
                    while (length > 0) {
                        var r = stream.Read(buffer, offset, length);
                        if (r == 0)
                            throw new EndOfStreamException();
                        if ((r < 0) || (r > length))
                            throw new Exception("Read returned unexpected value " + r);
                        length -= r;
                        offset += r;
                    }
                    LoadCompiledProgram(bytes, fileName);
                }
                return;
            }
            // It's source code, either a single node in text form or a JSON file
            string inputString;
            using (var reader = new StreamReader(fileName)) {
                inputString = reader.ReadToEnd();
            }

            LoadString(inputString, fileName, showTokens, showParseTree, onlyConsiderNode);
        }

        public void LoadCompiledProgram(MemoryStream stream, string fileName, CompiledFormat format = LATEST_FORMAT) {
            if (LogDebugMessage == null) {
                throw new YarnException("LogDebugMessage must be set before loading");
            }

            if (LogErrorMessage == null) {
                throw new YarnException("LogErrorMessage must be set before loading");
            }

            switch (format) {
                case CompiledFormat.V1:
                    LoadCompiledProgramV1(stream);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        void LoadCompiledProgramV1(MemoryStream stream) {
            var reader = new BsonReader(stream);
            var serializer = new JsonSerializer();

            try {
                // Load the stored program
                var newProgram = serializer.Deserialize<Program>(reader);

                // Merge it with our existing one, if present
                if (program != null) {
                    program.Include(newProgram);
                }
                else {
                    program = newProgram;
                }
            }
            catch (JsonReaderException e) {
                LogErrorMessage(string.Format("Cannot load compiled program: {0}", e.Message));
            }
        }

        // Ask the loader to parse a string. Returns the number of nodes that were loaded.
        public void LoadString(string text, string fileName = "<input>", bool showTokens = false, bool showParseTree = false, string onlyConsiderNode = null) {
            if (LogDebugMessage == null) {
                throw new YarnException("LogDebugMessage must be set before loading");
            }

            if (LogErrorMessage == null) {
                throw new YarnException("LogErrorMessage must be set before loading");
            }

            // Try to infer the type

            NodeFormat format;

            if (text.StartsWith("[", StringComparison.Ordinal)) {
                // starts with a {? this is probably a JSON array
                format = NodeFormat.JSON;
            }
            else if (text.Contains("---")) {
                // contains a --- delimiter? probably multi node text
                format = NodeFormat.Text;
            }
            else {
                // fall back to the single node format
                format = NodeFormat.SingleNodeText;
            }

            program = loader.Load(text, library, fileName, program, showTokens, showParseTree, onlyConsiderNode, format);
        }

        VirtualMachine vm;

        // Executes a node. Use this in a for-each construct; each time you iterate over it,
        // you'll get a line, command, or set of options.
        public IEnumerable<RunnerResult> Run(string startNode = DEFAULT_START) {
            if (LogDebugMessage == null) {
                throw new YarnException("LogDebugMessage must be set before running");
            }

            if (LogErrorMessage == null) {
                throw new YarnException("LogErrorMessage must be set before running");
            }

            if (program == null) {
                LogErrorMessage("Dialogue.Run was called, but no program was loaded. Stopping.");
                yield break;
            }

            vm = new VirtualMachine(this, program);

            RunnerResult latestResult;

            vm.lineHandler = delegate(LineResult result) { latestResult = result; };

            vm.commandHandler = delegate(CommandResult result) {
                // Is it the special custom command "<<stop>>"?
                if (result != null && result.command.text == "stop") {
                    vm.Stop();
                }
                latestResult = result;
            };

            vm.nodeCompleteHandler = delegate(NodeCompleteResult result) {
                // get the count if it's there, otherwise it defaults to 0
                int count;
                visitedNodeCount.TryGetValue(vm.currentNodeName, out count);

                visitedNodeCount[vm.currentNodeName] = count + 1;
                latestResult = result;
            };

            vm.optionsHandler = delegate(OptionSetResult result) { latestResult = result; };

            if (vm.SetNode(startNode) == false) {
                yield break;
            }

            // Run until the program stops, pausing to yield important
            // results
            do {
                latestResult = null;
                vm.RunNext();
                if (latestResult != null)
                    yield return latestResult;
            } while (vm.executionState != VirtualMachine.ExecutionState.Stopped);
        }

        public void Stop() {
            if (vm != null)
                vm.Stop();
        }

        public IEnumerable<string> visitedNodes { get { return visitedNodeCount.Keys; } }

        public IEnumerable<string> allNodes { get { return program.nodes.Keys; } }

        public string currentNode {
            get {
                if (vm == null) {
                    return null;
                }
                return vm.currentNodeName;
            }
        }

        public Dictionary<string, string> GetTextForAllNodes() {
            var d = new Dictionary<string, string>();

            foreach (var node in program.nodes) {
                var text = program.GetTextForNode(node.Key);

                if (text == null)
                    continue;

                d[node.Key] = text;
            }

            return d;
        }

        // Returns the source code for the node 'nodeName', 
        // if that node was tagged with rawText.
        public string GetTextForNode(string nodeName) {
            if (program.nodes.Count == 0) {
                LogErrorMessage("No nodes are loaded!");
                return null;
            }
            if (program.nodes.ContainsKey(nodeName)) {
                return program.GetTextForNode(nodeName);
            }
            LogErrorMessage("No node named " + nodeName);
            return null;
        }

        public void AddStringTable(Dictionary<string, string> stringTable) {
            program.LoadStrings(stringTable);
        }

        public Dictionary<string, string> GetStringTable() {
            return program.strings;
        }

        public Dictionary<string, LineInfo> GetStringInfoTable() {
            return program.lineInfo;
        }

        public enum CompiledFormat {
            V1
        }

        public const CompiledFormat LATEST_FORMAT = CompiledFormat.V1;

        public byte[] GetCompiledProgram(CompiledFormat format = LATEST_FORMAT) {
            switch (format) {
                case CompiledFormat.V1:
                    return GetCompiledProgramV1();
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        byte[] GetCompiledProgramV1() {
            using (var outputStream = new MemoryStream()) {
                using (var bsonWriter = new BsonWriter(outputStream)) {
                    var s = new JsonSerializer();
                    s.Serialize(bsonWriter, program);
                }

                return outputStream.ToArray();
            }
        }

        // Unloads ALL nodes.
        public void UnloadAll(bool clearVisitedNodes = true) {
            if (clearVisitedNodes)
                visitedNodeCount.Clear();

            program = null;
        }

        [Obsolete("Calling Compile() is no longer necessary.")]
        public String Compile() {
            return program.DumpCode(library);
        }

        public String GetByteCode() {
            return program.DumpCode(library);
        }

        public bool NodeExists(string nodeName) {
            if (program == null) {
                if (program.nodes.Count > 0) {
                    LogErrorMessage("Internal consistency error: Called NodeExists, and " +
                                    "there are nodes loaded, but the program hasn't " +
                                    "been compiled yet, somehow?");

                    return false;
                }
                LogErrorMessage("Tried to call NodeExists, but no nodes " +
                                "have been compiled!");
                return false;
            }
            if (program.nodes == null || program.nodes.Count == 0) {
                LogDebugMessage("Called NodeExists, but there are zero nodes. " +
                                "This may be an error.");
                return false;
            }
            return program.nodes.ContainsKey(nodeName);
        }

        public void Analyse(Context context) {
            context.AddProgramToAnalysis(program);
        }


        // The standard, built-in library of functions and operators.
        class StandardLibrary : Library {
            public StandardLibrary() {
                #region Operators

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Add], 2, delegate(Value[] parameters) { return parameters[0] + parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Minus], 2, delegate(Value[] parameters) { return parameters[0] - parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.UnaryMinus], 1, delegate(Value[] parameters) { return -parameters[0]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Divide], 2, delegate(Value[] parameters) { return parameters[0] / parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Multiply], 2, delegate(Value[] parameters) { return parameters[0] * parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.EqualTo], 2, delegate(Value[] parameters) { return parameters[0].Equals(parameters[1]); });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.NotEqualTo], 2, delegate(Value[] parameters) {
                    // Return the logical negative of the == operator's result
                    var equalTo = GetFunction(TokenTypeHelper.Strings[(int)TokenType.EqualTo]);

                    return !equalTo.Invoke(parameters).AsBool;
                });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.GreaterThan], 2, delegate(Value[] parameters) { return parameters[0] > parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.GreaterThanOrEqualTo], 2, delegate(Value[] parameters) { return parameters[0] >= parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.LessThan], 2, delegate(Value[] parameters) { return parameters[0] < parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.LessThanOrEqualTo], 2, delegate(Value[] parameters) { return parameters[0] <= parameters[1]; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.And], 2, delegate(Value[] parameters) { return parameters[0].AsBool && parameters[1].AsBool; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Or], 2, delegate(Value[] parameters) { return parameters[0].AsBool || parameters[1].AsBool; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Xor], 2, delegate(Value[] parameters) { return parameters[0].AsBool ^ parameters[1].AsBool; });

                RegisterFunction(TokenTypeHelper.Strings[(int)TokenType.Not], 1, delegate(Value[] parameters) { return !parameters[0].AsBool; });

                #endregion Operators
            }
        }
    }
}
