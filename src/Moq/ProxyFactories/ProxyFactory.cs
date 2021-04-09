// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD, and Contributors.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Reflection;

namespace Moq
{
	/// <summary>
	/// Horrible, horrible, ugly, terrible, no-good hack! Go away! You did not see this!
	/// </summary>
	public static class ProxyCache
	{
		/// <summary>
		/// Dude, why are you even here? You are not supposed to be looking at this. Get lost. Use nuget!
		/// </summary>
		public static void Invalidate() => ProxyFactory.InvalidateInstance();
	}

	internal abstract class ProxyFactory
	{
		/// <summary>
		/// Gets the global <see cref="ProxyFactory"/> instance used by Moq.
		/// </summary>
		public static ProxyFactory Instance { get; private set; } = new CastleProxyFactory();
		/// <summary>
		/// This is the unofficial hack. This is not approved. Use it at your own risk and if it breaks,
		/// congratulations you have bought the farm!
		/// </summary>
		internal static void InvalidateInstance() => Instance = new CastleProxyFactory();

		public abstract object CreateProxy(Type mockType, IInterceptor interceptor, Type[] interfaces, object[] arguments);

		public abstract bool IsMethodVisible(MethodInfo method, out string messageIfNotVisible);

		public abstract bool IsTypeVisible(Type type);
	}
}
