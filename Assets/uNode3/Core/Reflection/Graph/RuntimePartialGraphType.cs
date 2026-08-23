using System;

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
	/// Member merging itself is shared with the base families through
	/// <see cref="PartialGraphMerge"/>, which the base Build methods already invoke.
	/// </summary>
	public class RuntimePartialGraphType : RuntimeGraphType, IPartialGraphType, INativeType {
		public RuntimePartialGraphType(GraphAsset target) : base(target) { }

		GraphAsset IPartialGraphType.ownerAsset => target;

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
