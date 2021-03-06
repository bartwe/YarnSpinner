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

// Comment out to not catch exceptions

#define CATCH_EXCEPTIONS

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Linq;

namespace Yarn {
    public enum NodeFormat {
        Unknown, // an unknown type

        SingleNodeText, // a plain text file containing a single node with no metadata

        JSON, // a JSON file containing multiple nodes with metadata

        Text, //  a text file containing multiple nodes with metadata
    }


    public class Loader {
        readonly Dialogue dialogue;

        public Program program { get; private set; }

        // Prints out the list of tokens that the tokeniser found for this node
        void PrintTokenList(IEnumerable<Token> tokenList) {
            // Sum up the result
            var sb = new StringBuilder();
            foreach (var t in tokenList) {
                sb.AppendLine(string.Format("{0} ({1} line {2})", t, t.context, t.lineNumber));
            }

            // Let's see what we got
            dialogue.LogDebugMessage("Tokens:");
            dialogue.LogDebugMessage(sb.ToString());
        }

        // Prints the parse tree for the node
        void PrintParseTree(Parser.ParseNode rootNode) {
            dialogue.LogDebugMessage("Parse Tree:");
            dialogue.LogDebugMessage(rootNode.PrintTree(0));
        }

        // Prepares a loader. 'implementation' is used for logging.
        public Loader(Dialogue dialogue) {
            if (dialogue == null)
                throw new ArgumentNullException("dialogue");

            this.dialogue = dialogue;
        }

        // Given a bunch of raw text, load all nodes that were inside it.
        // You can call this multiple times to append to the collection of nodes,
        // but note that new nodes will replace older ones with the same name.
        // Returns the number of nodes that were loaded.
        public Program Load(string text, Library library, string fileName, Program includeProgram, bool showTokens, bool showParseTree, string onlyConsiderNode, NodeFormat format) {
            // The final parsed nodes that were in the file we were given
            var nodes = new Dictionary<string, Parser.Node>();

            // Load the raw data and get the array of node title-text pairs

            if (format == NodeFormat.Unknown) {
                format = GetFormatFromFileName(fileName);
            }


            var nodeInfos = GetNodesFromText(text, format);

            var nodesLoaded = 0;

            foreach (var nodeInfo in nodeInfos) {
                if (onlyConsiderNode != null && nodeInfo.title != onlyConsiderNode)
                    continue;

                // Attempt to parse every node; log if we encounter any errors
#if CATCH_EXCEPTIONS
                try {
#endif

                    if (nodes.ContainsKey(nodeInfo.title)) {
                        throw new InvalidOperationException("Attempted to load a node called " +
                                                            nodeInfo.title + ", but a node with that name has already been loaded!");
                    }

                    var lexer = new Lexer();
                    var tokens = lexer.Tokenise(nodeInfo.title, nodeInfo.body);

                    if (showTokens)
                        PrintTokenList(tokens);

                    var node = new Parser(tokens, library).Parse();

                    // If this node is tagged "rawText", then preserve its source
                    if (string.IsNullOrEmpty(nodeInfo.tags) == false &&
                        nodeInfo.tags.Contains("rawText")) {
                        node.source = string.Join("\\n", nodeInfo.body);
                    }

                    node.name = nodeInfo.title;

                    node.nodeTags = nodeInfo.tagsList;

                    if (showParseTree)
                        PrintParseTree(node);

                    nodes[nodeInfo.title] = node;

                    nodesLoaded++;

#if CATCH_EXCEPTIONS
                }
                catch (TokeniserException t) {
                    // Add file information
                    var message = string.Format("In file {0}: Error reading node {1}: {2}", fileName, nodeInfo.title, t.Message);
                    throw new TokeniserException(message);
                }
                catch (ParseException p) {
                    var message = string.Format("In file {0}: Error parsing node {1}: {2}", fileName, nodeInfo.title, p.Message);
                    throw new ParseException(message);
                }
                catch (InvalidOperationException e) {
                    var message = string.Format("In file {0}: Error reading node {1}: {2}", fileName, nodeInfo.title, e.Message);
                    throw new InvalidOperationException(message);
                }
#endif
            }

            var compiler = new Compiler(fileName);

            foreach (var node in nodes) {
                compiler.CompileNode(node.Value);
            }

            if (includeProgram != null) {
                compiler.program.Include(includeProgram);
            }

            return compiler.program;
        }

        // The raw text of the Yarn node, plus metadata
        // All properties are serialised except tagsList, which is a derived property
        [JsonObject(MemberSerialization.OptOut)]
        public struct NodeInfo {
            public struct Position {
                public int x { get; set; }
                public int y { get; set; }
            }

            public string title { get; set; }
            public string[] body { get; set; }

            // The raw "tags" field, containing space-separated tags. This is written
            // to the file.
            public string tags { get; set; }

            public int colorID { get; set; }
            public Position position { get; set; }

            // The tags for this node, as a list of individual strings.
            [JsonIgnore]
            public List<string> tagsList
            {
                get
                {
                    // If we have no tags list, or it's empty, return the empty list
                    if (string.IsNullOrEmpty(tags)) {
                        return new List<string>();
                    }

                    return new List<string>(tags.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }

        internal static NodeFormat GetFormatFromFileName(string fileName) {
            NodeFormat format;
            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
                format = NodeFormat.JSON;
            }
            else if (fileName.EndsWith(".yarn.txt", StringComparison.OrdinalIgnoreCase)) {
                format = NodeFormat.Text;
            }
            else if (fileName.EndsWith(".node", StringComparison.OrdinalIgnoreCase)) {
                format = NodeFormat.SingleNodeText;
            }
            else {
                throw new FormatException(string.Format("Unknown file format for file '{0}'", fileName));
            }

            return format;
        }

        // Given either Twine, JSON or XML input, return an array
        // containing info about the nodes in that file
        public NodeInfo[] GetNodesFromText(string text, NodeFormat format) {
            // All the nodes we found in this file
            var nodes = new List<NodeInfo>();

            switch (format) {
                case NodeFormat.SingleNodeText:
                    // If it starts with a comment, treat it as a single-node file
                    var nodeInfo = new NodeInfo();
                    nodeInfo.title = "Start";
                    nodeInfo.body = text.Split('\n');
                    nodes.Add(nodeInfo);
                    break;
                case NodeFormat.JSON:
                    try {
                        nodes = JsonConvert.DeserializeObject<List<NodeInfo>>(text);
                    } catch (JsonReaderException e) {
                        dialogue.LogErrorMessage("Error parsing Yarn input: " + e.Message);
                    }

                    break;
                case NodeFormat.Text:

                    // check for the existence of at least one "---"+newline sentinel, which divides
                    // the headers from the body

                    // we use a regex to match either \r\n or \n line endings
                    if (Regex.IsMatch(text, "---.?\n") == false) {
                        dialogue.LogErrorMessage("Error parsing input: text appears corrupt (no header sentinel");
                        break;
                    }

                    var headerRegex = new Regex("(?<field>.*): *(?<value>.*)");

                    var nodeProperties = typeof(NodeInfo).GetProperties();

                    var lineNumber = 0;

                    using (var reader = new StringReader(text)) {
                        string line;
                        while ((line = reader.ReadLine()) != null) {
                            // Create a new node
                            var node = new NodeInfo();

                            // Read header lines
                            do {
                                lineNumber++;

                                // skip empty lines
                                if (line.Length == 0) {
                                    continue;
                                }

                                // Attempt to parse the header
                                var headerMatches = headerRegex.Match(line);

                                if (headerMatches == null) {
                                    dialogue.LogErrorMessage(string.Format("Line {0}: Can't parse header '{1}'", lineNumber, line));
                                    continue;
                                }

                                var field = headerMatches.Groups["field"].Value;
                                var value = headerMatches.Groups["value"].Value;

                                // Attempt to set the appropriate property using this field
                                foreach (var property in nodeProperties) {
                                    if (property.Name != field) {
                                        continue;
                                    }

                                    // skip properties that can't be written to
                                    if (property.CanWrite == false) {
                                        continue;
                                    }
                                    try {
                                        var propertyType = property.PropertyType;
                                        object convertedValue;
                                        if (propertyType.IsAssignableFrom(typeof(string))) {
                                            convertedValue = value;
                                        }
                                        else if (propertyType.IsAssignableFrom(typeof(int))) {
                                            convertedValue = int.Parse(value);
                                        }
                                        else if (propertyType.IsAssignableFrom(typeof(NodeInfo.Position))) {
                                            var components = value.Split(',');

                                            // we expect 2 components: x and y
                                            if (components.Length != 2) {
                                                throw new FormatException();
                                            }

                                            var position = new NodeInfo.Position();
                                            position.x = int.Parse(components[0]);
                                            position.y = int.Parse(components[1]);

                                            convertedValue = position;
                                        }
                                        else {
                                            throw new NotSupportedException();
                                        }
                                        // we need to box this because structs are value types,
                                        // so calling SetValue using 'node' would just modify a copy of 'node'
                                        object box = node;
                                        property.SetValue(box, convertedValue, null);
                                        node = (NodeInfo)box;
                                        break;
                                    }
                                    catch (FormatException) {
                                        dialogue.LogErrorMessage(string.Format("{0}: Error setting '{1}': invalid value '{2}'", lineNumber, field, value));
                                    }
                                    catch (NotSupportedException) {
                                        dialogue.LogErrorMessage(string.Format("{0}: Error setting '{1}': This property cannot be set", lineNumber, field));
                                    }
                                }
                            } while ((line = reader.ReadLine()) != "---");

                            lineNumber++;

                            // We're past the header; read the body

                            var lines = new List<string>();

                            // Read header lines until we hit the end of node sentinel or the end of the file
                            while ((line = reader.ReadLine()) != "===" && line != null) {
                                lineNumber++;
                                lines.Add(line);
                            }
                            // We're done reading the lines! Zip 'em up into a string and
                            // store it in the body
                            node.body = lines.ToArray();

                            // And add this node to the list
                            nodes.Add(node);

                            // And now we're ready to move on to the next line!
                        }
                    }
                    break;
                default:
                    throw new InvalidOperationException();
            }

            // hooray we're done
            return nodes.ToArray();
        }
    }

}
