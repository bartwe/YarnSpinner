﻿using NUnit.Framework;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Yarn;

namespace YarnSpinner.Tests
{


	[TestFixture()]
	public class ProjectTests : TestBase
	{

		[Test()]
		public void TestLoadingSingleFile()
		{
			var path = Path.Combine("Projects", "Basic", "Test.yarn.txt");

			// high-level test: load the file, verify it has the nodes we want,
			// and run one
			dialogue.LoadFile(path);

			Assert.AreEqual(3, dialogue.program.nodes.Count);

			Assert.True(dialogue.NodeExists("Test node"));
			Assert.True(dialogue.NodeExists("Another test node"));
			Assert.True(dialogue.NodeExists("Third node"));

			// execute a node
			var secondNodeName = "Another test node";
			ExpectLine("This is a second test node!");

			// we are now expecting a node
			Assert.IsTrue(this.isExpectingLine);

			RunStandardTestcase(secondNodeName);

			// the line should have run, so this property should become true
			Assert.IsFalse(this.isExpectingLine);

			// and the variable $x should now have a value
			Assert.AreEqual(23, (int)dialogue.continuity.GetValue("$x").AsNumber);

			// Next, we'll do a lower-level test, verifying that properties are loaded correctly

			// manually load the NodeInfos and verify data
			var text = File.ReadAllText(path);

			var nodes = dialogue.loader.GetNodesFromText(text, NodeFormat.Text);

			// first node has a colorID
			Assert.AreEqual(3, nodes[0].colorID);

			// second node has got a position defined
			var position = nodes[1].position;
			Assert.AreEqual(2, position.x);
			Assert.AreEqual(4, position.y);

			// third node has tags
			var expectedTags = new List<string>(new string[] { "multiple", "tags!"});

			Assert.AreEqual(expectedTags, nodes[2].tagsList);

			// the third node's body is empty
			Assert.IsEmpty(nodes[2].body);



		}
	}
}