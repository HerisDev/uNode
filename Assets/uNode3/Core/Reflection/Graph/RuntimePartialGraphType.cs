using System;
using System.Collections.Generic;
using System.Reflection;

namespace MaxyGames.UNode {
	/// <summary>
	/// Marks a runtime type that presents the combined view of a `partial` graph class:
	/// the owning half's authored members plus every sibling half and the compiled
	/// hand-written C# class when one exists.
	/// </summary>
	public interface IPartialGraphType {
		/// <summary>
		/// The graph asset whose half this instance was first created from.
		/// </summary>
		GraphAsset ownerAsset { get; }
	}

	/// <summary>
	/// The one runtime type of a `partial` graph class.
	/// One instance per full name is kept by the editor registry, so every half's
	/// <see cref="IReflectionType.ReflectionType"/> resolves to this same object and all
	/// consumers see a single combined type.
	/// It implements <see cref="INativeType"/> because a partial class always has a real CLR
	/// presence: the merged generated class once produced, otherwise the compiled
	/// hand-written half while only it exists.
	/// The member builds below own the combining: base builds produce the owning half's
	/// authored members only, then every other half is merged on top through
	/// <see cref="PartialGraphMerge"/>.
	/// </summary>
	public class RuntimePartialGraphType : RuntimeGraphType, IPartialGraphType, INativeType {
		public RuntimePartialGraphType(GraphAsset target) : base(target) { }

		GraphAsset IPartialGraphType.ownerAsset => target;

		protected override void BuildFields() {
			base.BuildFields();
			PartialGraphMerge.AppendExternalFields(target, this, fields);
		}

		protected override void BuildProperties() {
			base.BuildProperties();
			PartialGraphMerge.AppendExternalProperties(target, this, properties);
		}

		protected override void BuildMethods() {
			base.BuildMethods();
			PartialGraphMerge.AppendExternalMethods(target, this, methods);
		}

		protected override void BuildEvents() {
			base.BuildEvents();
			var otherHalf = OtherHalfType;
			if(otherHalf != null) {
				foreach(var nativeEvent in otherHalf.GetEvents(MemberData.flags)) {
					events.Add(new RuntimeGraphExternalEvent(this, nativeEvent));
				}
			}
		}

		public override Type GetNativeType() {
			//The merged generated class once produced, otherwise the compiled hand-written
			//half while only it exists; fall back to the base proxy resolution last.
			var otherHalf = OtherHalfType;
			if(otherHalf != null)
				return otherHalf;
			return base.GetNativeType();
		}
	}
}
