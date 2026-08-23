using System;
using System.Collections.Generic;
using System.Reflection;

namespace MaxyGames.UNode {
	/// <summary>
	/// The one place where members of a `partial` graph's other halves are merged into a
	/// runtime type: the compiled hand-written class when it exists, plus every sibling
	/// graph sharing the same full name. Both interpreted (`RuntimeGraphType`) and native
	/// backed (`RuntimeNativeGraph`) types route through here, so they always combine
	/// identically and independently of whether a compiled other half exists yet.
	/// Graph-authored members always win: nothing here shadows an existing entry.
	/// Compiled members are additionally filtered by provenance through the source scan
	/// (<see cref="PartialGraphMembers.Get"/>): only declarations a human actually wrote are
	/// merged, everything the graph itself generated - including compiler artifacts like
	/// auto-property backing fields - is skipped, because the base builds already represent
	/// it through authored wrappers.
	/// </summary>
	internal static class PartialGraphMerge {
		public static void AppendExternalFields(GraphAsset self, RuntimeType owner, List<FieldInfo> fields) {
			var handWritten = GetHandWrittenKeys(self);
			var otherHalf = PartialGraphMembers.GetCompiledType(self);
			if(otherHalf != null) {
				foreach(var native in otherHalf.GetFields(MemberData.flags)) {
					//Auto-property backing fields and similar artifacts are never hand-written.
					if(native.Name.StartsWith("<", StringComparison.Ordinal))
						continue;
					if(handWritten != null && !handWritten.Contains("Field:" + native.Name))
						continue;
					if(fields.Exists(m => m.Name == native.Name))
						continue;
					fields.Add(new RuntimeGraphExternalField(owner, native));
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(self)) {
				var siblingTarget = PartialGraphMembers.GetOwnerAsset(sibling);
				if(siblingTarget == null)
					continue;
				foreach(var variable in siblingTarget.GetVariables()) {
					if(fields.Exists(m => m.Name == variable.name))
						continue;
					fields.Add(new RuntimeGraphField(owner, new VariableRef(variable)));
				}
			}
		}

		public static void AppendExternalProperties(GraphAsset self, RuntimeType owner, List<PropertyInfo> properties) {
			var handWritten = GetHandWrittenKeys(self);
			var otherHalf = PartialGraphMembers.GetCompiledType(self);
			if(otherHalf != null) {
				foreach(var native in otherHalf.GetProperties(MemberData.flags)) {
					if(handWritten != null && !handWritten.Contains("Property:" + native.Name))
						continue;
					if(properties.Exists(m => m.Name == native.Name))
						continue;
					properties.Add(new RuntimeGraphExternalProperty(owner, native));
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(self)) {
				var siblingTarget = PartialGraphMembers.GetOwnerAsset(sibling);
				if(siblingTarget == null)
					continue;
				foreach(var property in siblingTarget.GetProperties()) {
					if(properties.Exists(m => m.Name == property.name))
						continue;
					properties.Add(new RuntimeGraphProperty(owner, new PropertyRef(property)));
				}
			}
		}

		public static void AppendExternalMethods(GraphAsset self, RuntimeType owner, List<MethodInfo> methods) {
			var handWritten = GetHandWrittenKeys(self);
			var otherHalf = PartialGraphMembers.GetCompiledType(self);
			if(otherHalf != null) {
				foreach(var native in otherHalf.GetMethods(MemberData.flags)) {
					if(native.IsSpecialName)
						continue;
					if(native.Name.StartsWith("<", StringComparison.Ordinal))
						continue;
					if(handWritten != null && !handWritten.Contains(MakeSignature(native)))
						continue;
					//Methods are matched on the full signature so overloads are kept.
					if(methods.Exists(m => m.Name == native.Name && SameParameters(m, native)))
						continue;
					methods.Add(new RuntimeGraphExternalMethod(owner, native));
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(self)) {
				var siblingTarget = PartialGraphMembers.GetOwnerAsset(sibling);
				if(siblingTarget == null)
					continue;
				foreach(var function in siblingTarget.GetFunctions()) {
					//Functions are matched on name and parameter types so overloads stay legal.
					if(methods.Exists(m => m.Name == function.name && SameParameters(m, function)))
						continue;
					methods.Add(new RuntimeGraphMethod(owner, new FunctionRef(function)));
				}
			}
		}

		/// <summary>
		/// The lookup keys of hand-written declarations found by scanning the class source,
		/// used as the provenance filter for compiled members: fields and properties are
		/// keyed as `Kind:name`, methods through <see cref="PartialMemberInfo.Signature"/>.
		/// Returns null when no scanner is installed (player builds), which makes callers
		/// keep their legacy merging behavior - players run native mode where real CLR
		/// members exist regardless.
		/// </summary>
		private static HashSet<string> GetHandWrittenKeys(GraphAsset self) {
			if(PartialGraphMembers.provider == null)
				return null;
			var keys = new HashSet<string>(StringComparer.Ordinal);
			var members = PartialGraphMembers.Get(self);
			if(members != null) {
				foreach(var member in members) {
					if(member.kind == PartialMemberKind.Method) {
						keys.Add(member.Signature());
					}
					else {
						keys.Add(member.kind + ":" + member.name);
					}
				}
			}
			return keys;
		}

		/// <summary>
		/// The signature key of a compiled method, shaped exactly like
		/// <see cref="PartialMemberInfo.Signature"/> so both sides compare equal.
		/// By-ref parameters are unwrapped, since the scan stores plain parameter types.
		/// </summary>
		public static string MakeSignature(MethodInfo method) {
			var parameters = method.GetParameters();
			var result = method.Name + "(";
			for(int i = 0; i < parameters.Length; i++) {
				if(i != 0)
					result += ",";
				var type = parameters[i].ParameterType;
				if(type.IsByRef)
					type = type.GetElementType();
				result += type != null ? type.FullName : "?";
			}
			return result + ")";
		}

		public static bool SameParameters(MethodInfo method, MethodInfo native) {
			var parameters = method.GetParameters();
			var nativeParameters = native.GetParameters();
			if(parameters.Length != nativeParameters.Length)
				return false;
			for(int i = 0; i < parameters.Length; i++) {
				var type = parameters[i].ParameterType;
				if(type.IsByRef)
					type = type.GetElementType();
				if(type != nativeParameters[i].ParameterType)
					return false;
			}
			return true;
		}

		public static bool SameParameters(MethodInfo method, Function function) {
			var parameters = method.GetParameters();
			var functionParameters = function.Parameters;
			if(parameters.Length != functionParameters.Count)
				return false;
			for(int i = 0; i < parameters.Length; i++) {
				var type = parameters[i].ParameterType;
				if(type.IsByRef)
					type = type.GetElementType();
				if(type != functionParameters[i].Type)
					return false;
			}
			return true;
		}
	}
}
