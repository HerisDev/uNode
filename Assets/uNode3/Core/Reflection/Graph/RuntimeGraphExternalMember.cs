using System;
using System.Globalization;
using System.Reflection;

namespace MaxyGames.UNode {
	/// <summary>
	/// Shared helpers for members that live in the hand-written half of a `partial` graph.
	/// </summary>
	internal static class ExternalMemberUtility {
		/// <summary>
		/// True when the instance can legally receive a call on the hand-written half: only
		/// when it already is an instance of that half, ie. once the graph has been compiled
		/// to C# and merged with it.
		/// </summary>
		internal static bool IsNativeInstance(Type otherHalfType, object obj) {
			if(otherHalfType == null || obj == null)
				return false;
			if(obj is IInstancedGraph)
				return false;
			var type = obj.GetType();
			if(type is RuntimeType)
				return false;
			//The interpreted graph host (`RuntimeInstancedGraph`, `ClassObject`) is never the
			//hand-written type, so instance members cannot run against it.
			return otherHalfType.IsInstanceOfType(obj);
		}

		internal static Exception NotInstanceExecutable(RuntimeType owner, string name) {
			return new Exception(
				$"`{name}` is declared in the hand-written half of the partial class `{owner.PrettyName(true)}`, " +
				"so it needs a real instance of that class.\n" +
				"It can be wired up in the graph, but it cannot be executed in reflection (interpreted) mode. " +
				"Compile the graph to C# to merge both halves into one class and run it.");
		}
	}

	/// <summary>
	/// A field declared in the hand-written half of a `partial` graph, backed by the real
	/// <see cref="FieldInfo"/> of the compiled hand-written class.
	/// Static fields read and write directly; instance fields require the graph to be
	/// compiled to C#, because only then does one object hold both halves.
	/// </summary>
	public class RuntimeGraphExternalField : RuntimeField {
		public readonly FieldInfo native;

		public RuntimeGraphExternalField(RuntimeType owner, FieldInfo native) : base(owner) {
			this.native = native ?? throw new ArgumentNullException(nameof(native));
		}

		public override string Name => native.Name;
		public override Type FieldType => native.FieldType;
		public override FieldAttributes Attributes => native.Attributes;

		public override object[] GetCustomAttributes(bool inherit) => native.GetCustomAttributes(inherit);

		public override object[] GetCustomAttributes(Type attributeType, bool inherit) => native.GetCustomAttributes(attributeType, inherit);

		public override object GetValue(object obj) {
			if(native.IsStatic || ExternalMemberUtility.IsNativeInstance(native.DeclaringType, obj))
				return native.GetValue(obj);
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}

		public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture) {
			if(native.IsStatic || ExternalMemberUtility.IsNativeInstance(native.DeclaringType, obj)) {
				native.SetValue(obj, value, invokeAttr, binder, culture);
				return;
			}
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}
	}

	/// <summary>
	/// A property declared in the hand-written half of a `partial` graph, backed by the real
	/// <see cref="PropertyInfo"/> of the compiled hand-written class.
	/// </summary>
	public class RuntimeGraphExternalProperty : RuntimeProperty {
		public readonly PropertyInfo native;

		public RuntimeGraphExternalProperty(RuntimeType owner, PropertyInfo native) : base(owner) {
			this.native = native ?? throw new ArgumentNullException(nameof(native));
		}

		public override string Name => native.Name;
		public override Type PropertyType => native.PropertyType;
		public override bool CanRead => native.CanRead;
		public override bool CanWrite => native.CanWrite;

		public override MethodInfo GetGetMethod(bool nonPublic) => native.GetGetMethod(nonPublic);

		public override MethodInfo GetSetMethod(bool nonPublic) => native.GetSetMethod(nonPublic);

		public override MethodInfo[] GetAccessors(bool nonPublic) => native.GetAccessors(nonPublic);

		public override object[] GetCustomAttributes(bool inherit) => native.GetCustomAttributes(inherit);

		public override object[] GetCustomAttributes(Type attributeType, bool inherit) => native.GetCustomAttributes(attributeType, inherit);

		private bool Executable(object obj) {
			return (native.GetGetMethod(true) ?? native.GetSetMethod(true)).IsStatic
				|| ExternalMemberUtility.IsNativeInstance(native.DeclaringType, obj);
		}

		public override object GetValue(object obj, BindingFlags invokeAttr, Binder binder, object[] index, CultureInfo culture) {
			if(Executable(obj))
				return native.GetValue(obj, invokeAttr, binder, index, culture);
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}

		public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, object[] index, CultureInfo culture) {
			if(Executable(obj)) {
				native.SetValue(obj, value, invokeAttr, binder, index, culture);
				return;
			}
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}
	}

	/// <summary>
	/// A method declared in the hand-written half of a `partial` graph, backed by the real
	/// <see cref="MethodInfo"/> of the compiled hand-written class.
	/// Static methods execute directly in reflection mode; instance methods require the
	/// graph to be compiled to C# first.
	/// </summary>
	public class RuntimeGraphExternalMethod : RuntimeMethod {
		public readonly MethodInfo native;

		public RuntimeGraphExternalMethod(RuntimeType owner, MethodInfo native) : base(owner) {
			this.native = native ?? throw new ArgumentNullException(nameof(native));
		}

		public override string Name => native.Name;
		public override Type ReturnType => native.ReturnType;

		public override MethodAttributes Attributes => native.Attributes;

		public override ParameterInfo[] GetParameters() => native.GetParameters();

		public override object[] GetCustomAttributes(bool inherit) => native.GetCustomAttributes(inherit);

		public override object[] GetCustomAttributes(Type attributeType, bool inherit) => native.GetCustomAttributes(attributeType, inherit);

		public override object Invoke(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) {
			if(native.IsStatic || ExternalMemberUtility.IsNativeInstance(native.DeclaringType, obj))
				return native.Invoke(obj, invokeAttr, binder, parameters, culture);
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}
	}

	/// <summary>
	/// An event declared in the hand-written half of a `partial` graph, backed by the real
	/// <see cref="EventInfo"/> of the compiled hand-written class.
	/// Static events subscribe directly in reflection mode; instance events require the
	/// graph to be compiled to C# first, like the other external members.
	/// </summary>
	public class RuntimeGraphExternalEvent : RuntimeEvent {
		public readonly EventInfo native;

		public RuntimeGraphExternalEvent(RuntimeType owner, EventInfo native) : base(owner) {
			this.native = native ?? throw new ArgumentNullException(nameof(native));
		}

		public override string Name => native.Name;
		public override Type EventHandlerType => native.EventHandlerType;
		public override EventAttributes Attributes => native.Attributes;

		private bool Executable(object obj) {
			var accessor = native.GetAddMethod(true) ?? native.GetRemoveMethod(true);
			return accessor == null || accessor.IsStatic || ExternalMemberUtility.IsNativeInstance(native.DeclaringType, obj);
		}

		public override void DoAddMethod(object instance, Delegate evt) {
			if(Executable(instance)) {
				native.AddEventHandler(instance, evt);
				return;
			}
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}

		public override void DoRemoveMethod(object instance, Delegate evt) {
			if(Executable(instance)) {
				native.RemoveEventHandler(instance, evt);
				return;
			}
			throw ExternalMemberUtility.NotInstanceExecutable(owner, Name);
		}

		public override void DoRaiseMethod(object instance) {
			//A native event cannot be raised from outside its declaring class.
		}

		public override object[] GetCustomAttributes(bool inherit) => native.GetCustomAttributes(inherit);

		public override object[] GetCustomAttributes(Type attributeType, bool inherit) => native.GetCustomAttributes(attributeType, inherit);

		public override bool IsDefined(Type attributeType, bool inherit) => native.IsDefined(attributeType, inherit);
	}
}
