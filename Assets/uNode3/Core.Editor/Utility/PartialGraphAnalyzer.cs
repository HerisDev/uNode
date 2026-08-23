using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MaxyGames.UNode.Editors.Analyzer {
	/// <summary>
	/// Validates the relationship between a `partial` graph and its other half, when one exists.
	/// A partial graph with no other half is a valid state and reports nothing: the merged
	/// graph type simply has nothing external to combine.
	/// Duplicate detection runs purely at source level (Roslyn): the hand-written half's
	/// declarations are compared against authored members directly, so genuine conflicts are
	/// always reported - regardless of whether anything was compiled yet - without confusing
	/// generated output with human declarations.
	/// </summary>
	class PartialGraphAnalyzer : GraphAnalyzer {
		public override bool IsValidAnalyzerForGraph(Type graphType) {
			return graphType.HasImplementInterface(typeof(IScriptGraphType));
		}

		public override void CheckGraphErrors(ErrorAnalyzer analyzer, IGraph graph) {
			var asset = graph as GraphAsset;
			if(asset == null)
				return;
			var graphData = graph.GraphData;
			if(graphData == null)
				return;
			var modifier = asset as IClassModifier;
			if(modifier == null)
				return;

			var name = asset.GetGraphName();
			if(string.IsNullOrEmpty(name))
				return;
			var ns = asset.GetGraphNamespace() ?? string.Empty;
			var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

			if(modifier.GetModifier().Partial == false) {
				CheckUnmarkedPartial(analyzer, graphData, asset, modifier, name, fullName);
				return;
			}

			var result = PartialGraphSourceScanner.Scan(asset);
			if(result == null || result.declarations.Count == 0)
				return;
			CheckCollisions(analyzer, graphData, asset, result, fullName);
		}

		/// <summary>
		/// A hand-written `partial` declaration exists but the graph does not generate one,
		/// so the two halves will not merge (CS0260).
		/// </summary>
		private void CheckUnmarkedPartial(
			ErrorAnalyzer analyzer, UGraphElement graphData, GraphAsset asset, IClassModifier modifier, string name, string fullName) {
			var paths = PartialGraphSourceScanner.FindOtherHalfDeclarationPaths(asset, name);
			if(paths.Count == 0)
				return;
			void autoFix() {
				modifier.GetModifier().Partial = true;
				uNodeEditorUtility.MarkDirty(graphData.graphContainer as UnityEngine.Object);
				PartialGraphSourceScanner.InvalidateCache();
			}
			analyzer.RegisterWarning(graphData,
				$"'{paths[0]}' declares a partial type named '{name}', but this graph is not marked 'Partial'.\n" +
				$"The generated '{fullName}' will not merge with it. Enable the 'Partial' class modifier to combine them.",
				autoFix);
		}

		/// <summary>
		/// Compares graph-authored members against the declarations found by scanning the
		/// hand-written half's source. Generated outputs are excluded from the scan, so they
		/// can never be mistaken for human-written duplicates.
		/// </summary>
		private void CheckCollisions(
			ErrorAnalyzer analyzer, UGraphElement graphData, GraphAsset asset,
			PartialGraphSourceScanner.ScanResult result, string fullName) {
			var declared = new Dictionary<string, string>();
			foreach(var variable in asset.GetVariables()) {
				declared[variable.name] = "variable";
			}
			foreach(var property in asset.GetProperties()) {
				declared[property.name] = "property";
			}
			foreach(var function in asset.GetFunctions()) {
				//Overloads are legal, so functions are keyed on their full signature.
				var key = function.name + "(" +
					string.Join(",", function.Parameters.Select(p => p.Type != null ? p.Type.FullName : "?")) + ")";
				declared[key] = "function";
			}
			foreach(var member in result.members) {
				var key = member.Signature();
				if(declared.TryGetValue(key, out var kind) == false)
					continue;
				//A bodyless `partial` function in the graph is a declaration, not a duplicate.
				if(member.kind == PartialMemberKind.Method && IsPartialDeclaration(asset, member))
					continue;
				analyzer.RegisterError(graphData,
					$"'{member.name}' is declared both by this graph ({kind}) and by the other half in " +
					$"'{member.sourcePath}'. '{fullName}' will not compile until one of them is removed.");
			}
		}

		/// <summary>
		/// True when the graph side of this method is a bodyless `partial` declaration,
		/// which is meant to be implemented by the other half.
		/// </summary>
		private bool IsPartialDeclaration(GraphAsset asset, PartialMemberInfo member) {
			foreach(var function in asset.GetFunctions()) {
				if(function.name != member.name)
					continue;
				if(function.modifier != null && function.modifier.Partial)
					return true;
			}
			return false;
		}
	}
}
