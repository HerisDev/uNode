using System;
using System.Collections.Generic;

namespace MaxyGames.UNode {
	/// <summary>
	/// The kind of member declared in the hand-written half of a partial graph.
	/// </summary>
	public enum PartialMemberKind {
		Field,
		Property,
		Method,
	}

	/// <summary>
	/// Describes a single parameter of a <see cref="PartialMemberInfo"/>.
	/// </summary>
	public class PartialParameterInfo {
		public string name;
		public Type type;
		public RefKind refKind;
		public bool hasDefaultValue;
		public object defaultValue;
	}

	/// <summary>
	/// Describes a member that exists in the hand-written half of a `partial` graph.
	/// These are discovered from the project source, they are not authored in the graph.
	/// </summary>
	public class PartialMemberInfo {
		public PartialMemberKind kind;
		public string name;
		/// <summary>
		/// The field/property type, or the return type of a method.
		/// </summary>
		public Type type;
		public PartialParameterInfo[] parameters = Array.Empty<PartialParameterInfo>();
		public bool isStatic;
		public bool isPublic = true;
		public bool canRead = true;
		public bool canWrite = true;
		public string summary;

		/// <summary>
		/// The source file this member was found in, for diagnostics.
		/// </summary>
		public string sourcePath;

		public Type[] ParameterTypes() {
			if(parameters == null || parameters.Length == 0)
				return Type.EmptyTypes;
			var result = new Type[parameters.Length];
			for(int i = 0; i < parameters.Length; i++) {
				result[i] = parameters[i].type;
			}
			return result;
		}

		/// <summary>
		/// A signature usable for de-duplicating against graph-authored members.
		/// </summary>
		public string Signature() {
			if(kind != PartialMemberKind.Method)
				return name;
			var result = name + "(";
			for(int i = 0; i < parameters.Length; i++) {
				if(i != 0)
					result += ",";
				result += parameters[i].type != null ? parameters[i].type.FullName : "?";
			}
			return result + ")";
		}
	}

	/// <summary>
	/// The seam between the runtime reflection types (this assembly) and the other half of a
	/// `partial` graph. Two views are available: <see cref="Get"/> for the syntax-scanned
	/// member descriptions (editor-provided), and <see cref="GetOtherHalfType"/> for the real
	/// compiled CLR type behind the class, resolved by full name right here so editor and
	/// builds behave identically.
	/// </summary>
	public static class PartialGraphMembers {
		private static readonly PartialMemberInfo[] none = Array.Empty<PartialMemberInfo>();

		/// <summary>
		/// Installed by the editor. Returns the members declared in the hand-written
		/// half of the given graph, or null when the graph is not partial.
		/// </summary>
		public static Func<GraphAsset, IList<PartialMemberInfo>> provider;

		/// <summary>
		/// Installed by the editor. Returns every other reflection type sharing this graph's
		/// full name, ie. the other `partial` graph halves of the same class.
		/// </summary>
		public static Func<GraphAsset, IList<RuntimeType>> siblingProvider;

		/// <summary>
		/// Installed by the editor. Returns the one <see cref="RuntimePartialGraphType"/>
		/// shared by all halves under this graph's full name, or null when unknown.
		/// Receives the caller's own raw instance so a fresh registry entry can be created
		/// when none exists yet.
		/// </summary>
		public static Func<IGraph, RuntimeType, RuntimeType> partialTypeProvider;

		/// <summary>
		/// The owning graph asset behind a runtime graph type, regardless of which wrapper
		/// kind presents it (`RuntimeGraphType`, `RuntimePartialGraphType` or legacy native
		/// wrappers).
		/// </summary>
		public static GraphAsset GetOwnerAsset(RuntimeType type) {
			if(type is IPartialGraphType partial)
				return partial.ownerAsset;
			if(type is RuntimeGraphType graphType)
				return graphType.target;
			if(type is RuntimeNativeGraph nativeGraph)
				return nativeGraph.target;
			return null;
		}

		/// <summary>
		/// The reflection type every consumer should see for the given partial graph: the one
		/// combined <see cref="RuntimePartialGraphType"/> under its full name. Returns `own`
		/// untouched for non-partial graphs or when no provider answered.
		/// </summary>
		public static RuntimeType GetReflectionType(IGraph self, RuntimeType own) {
			if(partialTypeProvider == null || self == null)
				return own;
			var modifier = self as IClassModifier;
			if(modifier == null || modifier.GetModifier()?.Partial != true)
				return own;
			try {
				return partialTypeProvider(self, own) ?? own;
			}
			catch(Exception ex) {
				UnityEngine.Debug.LogException(ex);
				return own;
			}
		}

		public static IList<PartialMemberInfo> Get(GraphAsset graph) {
			if(provider == null || graph == null)
				return none;
			try {
				return provider(graph) ?? none;
			}
			catch(Exception ex) {
				//Never let a scanner failure break reflection on the graph.
				UnityEngine.Debug.LogException(ex);
				return none;
			}
		}

		/// <summary>
		/// Memoises full-name resolutions. A domain reload clears it, which is exactly when
		/// assemblies change; renames mid-session simply orphan the old key.
		/// </summary>
		private static readonly Dictionary<string, Type> s_halfTypeCache = new Dictionary<string, Type>();

		/// <summary>
		/// The real CLR type behind a `partial` graph class, or null when nothing compiled
		/// carries that name yet (a normal state: being `partial` requires no other half).
		/// Resolution is by full name only, identical in editor and builds: the hand-written
		/// half and the generated merged class both compile with the project, so whichever
		/// exists is reachable through plain reflection. A resolved uNode runtime wrapper is
		/// rejected, since that would be another graph's type rather than a hand-written one.
		/// </summary>
		public static Type GetOtherHalfType(GraphAsset graph) {
			if(graph == null)
				return null;
			try {
				var modifier = graph as IClassModifier;
				if(modifier == null || modifier.GetModifier()?.Partial == false)
					return null;
				var name = graph.GetGraphName();
				if(string.IsNullOrEmpty(name))
					return null;
				var ns = graph.GetGraphNamespace() ?? string.Empty;
				var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
				if(s_halfTypeCache.TryGetValue(fullName, out var cached)) {
					return cached;
				}
				var compiled = fullName.ToType(false);
				//uNode runtime wrappers are never a hand-written half; they would only appear
				//here if the name collided with a graph type, which must not be merged.
				if(compiled is RuntimeType) {
					compiled = null;
				}
				s_halfTypeCache[fullName] = compiled;
				return compiled;
			}
			catch(Exception ex) {
				UnityEngine.Debug.LogException(ex);
				return null;
			}
		}

		/// <summary>
		/// The other graph halves of the same class: every partial reflection type whose full
		/// name equals this graph's. Empty when the graph is alone under its name, and in builds,
		/// where duplicate names cannot exist because the compiler would reject the merge.
		/// </summary>
		public static IList<RuntimeType> GetSiblingReflectionTypes(GraphAsset graph) {
			if(siblingProvider == null || graph == null)
				return Array.Empty<RuntimeType>();
			try {
				return siblingProvider(graph) ?? Array.Empty<RuntimeType>();
			}
			catch(Exception ex) {
				UnityEngine.Debug.LogException(ex);
				return Array.Empty<RuntimeType>();
			}
		}
	}
}
