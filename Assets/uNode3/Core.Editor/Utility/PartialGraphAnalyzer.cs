using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MaxyGames.UNode.Editors.Analyzer {
	/// <summary>
	/// Validates the relationship between a `partial` graph and its other half, when one exists.
	/// A partial graph with no other half is a valid state and reports nothing: the merged
	/// graph type simply has nothing external to combine.
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

			var otherHalf = PartialGraphMembers.GetOtherHalfType(asset);
			if(otherHalf != null) {
				CheckCollisionsWithNative(analyzer, graphData, asset, otherHalf, fullName);
				return;
			}

			//The other half exists in source but has not produced a usable CLR type yet
			//(fresh import or a script compile error): fall back to the syntax scan.
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
		/// Compares graph-authored members against the real compiled members of the other half,
		/// which is exact: every kind, visibility, overload and event is visible.
		/// Authored sets cover every graph half of the class, not just this one.
		/// When the compiled class originates from generation - this half or a sibling emitted
		/// its C# - authored members exist in it legitimately and overlaps are skipped; only
		/// genuinely foreign hand-written declarations are reported. Without generation the
		/// class is purely hand-written, so any overlap with an authored member is a real
		/// duplicate exactly as before.
		/// </summary>
		private void CheckCollisionsWithNative(
			ErrorAnalyzer analyzer, UGraphElement graphData, GraphAsset asset, Type otherHalf, string fullName) {
			bool generated = HasGeneratedOutput(asset);
			if(!generated) {
				foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(asset)) {
					var siblingOwner = PartialGraphMembers.GetOwnerAsset(sibling);
					if(siblingOwner != null && HasGeneratedOutput(siblingOwner)) {
						generated = true;
						break;
					}
				}
			}
			//Name-keyed map covering all single-member kinds; any overlap between kinds is
			//as much a duplicate as two fields of one name.
			var declared = new Dictionary<string, string>();
			foreach(var variable in asset.GetVariables()) {
				declared[variable.name] = "variable";
			}
			foreach(var property in asset.GetProperties()) {
				if(!declared.ContainsKey(property.name)) {
					declared[property.name] = "property";
				}
			}
			//Functions are matched on their full signature so overloads stay legal.
			var functionSignatures = new HashSet<string>();
			foreach(var function in asset.GetFunctions()) {
				functionSignatures.Add(MakeSignature(function.name, function.Parameters.Select(p => p.Type)));
				if(!declared.ContainsKey(function.name)) {
					declared[function.name] = "function";
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(asset)) {
				var owner = PartialGraphMembers.GetOwnerAsset(sibling);
				if(owner == null)
					continue;
				foreach(var variable in owner.GetVariables()) {
					if(!declared.ContainsKey(variable.name)) {
						declared[variable.name] = "variable";
					}
				}
				foreach(var property in owner.GetProperties()) {
					if(!declared.ContainsKey(property.name)) {
						declared[property.name] = "property";
					}
				}
				foreach(var function in owner.GetFunctions()) {
					functionSignatures.Add(MakeSignature(function.name, function.Parameters.Select(p => p.Type)));
					if(!declared.ContainsKey(function.name)) {
						declared[function.name] = "function";
					}
				}
			}
			//Overlaps with authored members are duplicates only when the compiled class is
			//purely hand-written; once any half generated its output they are legitimate.
			foreach(var nativeField in otherHalf.GetFields(MemberData.flags)) {
				if(IsCompilerGenerated(nativeField.Name))
					continue;
				if(declared.TryGetValue(nativeField.Name, out var kind)) {
					if(generated)
						continue;
					RegisterDuplicate(analyzer, graphData, fullName, nativeField.Name, "field", nativeField.DeclaringType, kind);
				}
			}
			foreach(var nativeProperty in otherHalf.GetProperties(MemberData.flags)) {
				if(declared.TryGetValue(nativeProperty.Name, out var kind)) {
					if(generated)
						continue;
					RegisterDuplicate(analyzer, graphData, fullName, nativeProperty.Name, "property", nativeProperty.DeclaringType, kind);
				}
			}
			foreach(var nativeEvent in otherHalf.GetEvents(MemberData.flags)) {
				if(declared.TryGetValue(nativeEvent.Name, out var kind)) {
					if(generated)
						continue;
					RegisterDuplicate(analyzer, graphData, fullName, nativeEvent.Name, "event", nativeEvent.DeclaringType, kind);
				}
			}
			foreach(var nativeMethod in otherHalf.GetMethods(MemberData.flags)) {
				if(nativeMethod.IsSpecialName)
					continue;
				var key = MakeSignature(nativeMethod.Name, nativeMethod.GetParameters().Select(p => p.ParameterType));
				if(functionSignatures.Contains(key)) {
					if(generated)
						continue;
					RegisterDuplicate(analyzer, graphData, fullName, nativeMethod.Name, "method", nativeMethod.DeclaringType, "function");
					continue;
				}
				//A method sharing its bare name with a non-method member will not compile either.
				if(declared.TryGetValue(nativeMethod.Name, out var kind) && kind != "function") {
					RegisterDuplicate(analyzer, graphData, fullName, nativeMethod.Name, "method", nativeMethod.DeclaringType, kind);
				}
			}
		}

		/// <summary>
		/// True when this asset has C# output produced by uNode: either the manual-compile
		/// convention of a script beside the asset, or the recorded location of its last
		/// generation. Such output compiles into the merged class, so everything it contains
		/// that matches an authored member is legitimate rather than a duplicate.
		/// </summary>
		private static bool HasGeneratedOutput(GraphAsset asset) {
			var assetPath = AssetDatabase.GetAssetPath(asset);
			if(!string.IsNullOrEmpty(assetPath)) {
				var beside = System.IO.Path.ChangeExtension(assetPath, ".cs");
				if(System.IO.File.Exists(beside))
					return true;
			}
			var data = GenerationUtility.GetGraphData(asset);
			if(data != null && !string.IsNullOrEmpty(data.path) && System.IO.File.Exists(data.path))
				return true;
			return false;
		}

		/// <summary>
		/// Fallback collision check over the syntax scan, used while the other half has no
		/// compiled type yet.
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

		private void RegisterDuplicate(
			ErrorAnalyzer analyzer, UGraphElement graphData, string fullName,
			string memberName, string halfKind, Type declaringType, string graphKind) {
			analyzer.RegisterError(graphData,
				$"'{memberName}' ({halfKind}) is declared both by this graph ({graphKind}) and by the other half in " +
				$"'{declaringType?.Assembly.GetName().Name ?? "source"}'. '{fullName}' will not compile until one of them is removed.");
		}

		private static string MakeSignature(string name, IEnumerable<Type> parameters) {
			return name + "(" + string.Join(",", parameters.Select(p => p != null ? p.FullName : "?")) + ")";
		}

		private static bool IsCompilerGenerated(string memberName) {
			//Auto-property backing fields (`<Foo>k__BackingField`) are never authored members.
			return !string.IsNullOrEmpty(memberName) && memberName.StartsWith("<");
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
