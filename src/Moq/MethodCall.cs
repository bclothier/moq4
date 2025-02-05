// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

using Moq.Properties;

using TypeNameFormatter;

namespace Moq
{
	internal sealed partial class MethodCall : SetupWithOutParameterSupport
	{
		private Action<object[]> afterReturnCallbackResponse;
		private Action<object[]> callbackResponse;
		private LimitInvocationCountResponse limitInvocationCountResponse;
		private Condition condition;
		private string failMessage;
		private Flags flags;
		private Mock mock;
		private RaiseEventResponse raiseEventResponse;
		private Response returnOrThrowResponse;

#if FEATURE_CALLERINFO
		private string declarationSite;
#endif

		public MethodCall(Mock mock, Condition condition, InvocationShape expectation)
			: base(expectation)
		{
			this.condition = condition;
			this.flags = expectation.Method.ReturnType != typeof(void) ? Flags.MethodIsNonVoid : 0;
			this.mock = mock;

#if FEATURE_CALLERINFO
			if ((mock.Switches & Switches.CollectDiagnosticFileInfoForSetups) != 0)
			{
				this.declarationSite = GetUserCodeCallSite();
			}
#endif
		}

		public string FailMessage
		{
			get => this.failMessage;
		}

		public Mock Mock => this.mock;

		public override Condition Condition => this.condition;

		public override bool IsVerifiable => (this.flags & Flags.Verifiable) != 0;

#if FEATURE_CALLERINFO
		private static string GetUserCodeCallSite()
		{
			try
			{
				var thisMethod = MethodBase.GetCurrentMethod();
				var mockAssembly = Assembly.GetExecutingAssembly();
				var frame = new StackTrace(true)
					.GetFrames()
					.SkipWhile(f => f.GetMethod() != thisMethod)
					.SkipWhile(f => f.GetMethod().DeclaringType == null || f.GetMethod().DeclaringType.Assembly == mockAssembly)
					.FirstOrDefault();
				var member = frame?.GetMethod();
				if (member != null)
				{
					var declaredAt = new StringBuilder();
					declaredAt.AppendNameOf(member.DeclaringType).Append('.').AppendNameOf(member, false);
					var fileName = Path.GetFileName(frame.GetFileName());
					if (fileName != null)
					{
						declaredAt.Append(" in ").Append(fileName);
						var lineNumber = frame.GetFileLineNumber();
						if (lineNumber != 0)
						{
							declaredAt.Append(": line ").Append(lineNumber);
						}
					}
					return declaredAt.ToString();
				}
			}
			catch
			{
				// Must NEVER fail, as this is a nice-to-have feature only.
			}

			return null;
		}
#endif

		public override void Execute(Invocation invocation)
		{
			this.flags |= Flags.Invoked;

			this.limitInvocationCountResponse?.RespondTo(invocation);

			this.callbackResponse?.Invoke(invocation.Arguments);

			if ((this.flags & Flags.CallBase) != 0)
			{
				invocation.ReturnBase();
			}

			this.raiseEventResponse?.RespondTo(invocation);

			this.returnOrThrowResponse?.RespondTo(invocation);

			if ((this.flags & Flags.MethodIsNonVoid) != 0)
			{
				if (this.returnOrThrowResponse == null)
				{
					if (this.Mock.Behavior == MockBehavior.Strict)
					{
						throw MockException.ReturnValueRequired(invocation);
					}
					else
					{
						// Instead of duplicating the entirety of `Return`'s implementation,
						// let's just call it here. This is permissible only if the inter-
						// ception pipeline will terminate right away (otherwise `Return`
						// might be executed a second time).
						Return.Handle(invocation, this.Mock);
					}
				}

				this.afterReturnCallbackResponse?.Invoke(invocation.Arguments);
			}
		}

		public override bool TryGetReturnValue(out object returnValue)
		{
			if (this.returnOrThrowResponse is ReturnEagerValueResponse revs)
			{
				returnValue = revs.Value;
				return true;
			}
			else
			{
				returnValue = default;
				return false;
			}
		}

		public void SetCallBaseResponse()
		{
			if (this.Mock.TargetType.IsDelegate())
			{
				throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.CallBaseCannotBeUsedWithDelegateMocks));
			}

			if ((this.flags & Flags.MethodIsNonVoid) != 0)
			{
				this.returnOrThrowResponse = ReturnBaseResponse.Instance;
			}
			else
			{
				this.flags |= Flags.CallBase;
			}
		}

		public void SetCallbackResponse(Delegate callback)
		{
			if (callback == null)
			{
				throw new ArgumentNullException(nameof(callback));
			}

			ref Action<object[]> response = ref this.returnOrThrowResponse == null ? ref this.callbackResponse
			                                                                       : ref this.afterReturnCallbackResponse;

			if (callback is Action callbackWithoutArguments)
			{
				response = (object[] args) => callbackWithoutArguments();
			}
			else
			{
				var expectedParamTypes = this.Method.GetParameterTypes();
				if (!callback.CompareParameterTypesTo(expectedParamTypes))
				{
					throw new ArgumentException(
						string.Format(
							CultureInfo.CurrentCulture,
							Resources.InvalidCallbackParameterMismatch,
							this.Method.GetParameterTypeList(),
							callback.GetMethodInfo().GetParameterTypeList()));
				}

				if (callback.GetMethodInfo().ReturnType != typeof(void))
				{
					throw new ArgumentException(Resources.InvalidCallbackNotADelegateWithReturnTypeVoid, nameof(callback));
				}

				response = (object[] args) => callback.InvokePreserveStack(args);
			}
		}

		public void SetRaiseEventResponse<TMock>(Action<TMock> eventExpression, Delegate func)
			where TMock : class
		{
			Guard.NotNull(eventExpression, nameof(eventExpression));

			var expression = ExpressionReconstructor.Instance.ReconstructExpression(eventExpression, this.mock.ConstructorArguments);

			// TODO: validate that expression is for event subscription or unsubscription

			this.raiseEventResponse = new RaiseEventResponse(this.mock, expression, func, null);
		}

		public void SetRaiseEventResponse<TMock>(Action<TMock> eventExpression, params object[] args)
			where TMock : class
		{
			Guard.NotNull(eventExpression, nameof(eventExpression));

			var expression = ExpressionReconstructor.Instance.ReconstructExpression(eventExpression, this.mock.ConstructorArguments);

			// TODO: validate that expression is for event subscription or unsubscription

			this.raiseEventResponse = new RaiseEventResponse(this.mock, expression, null, args);
		}

		public void SetEagerReturnsResponse(object value)
		{
			Debug.Assert((this.flags & Flags.MethodIsNonVoid) != 0);
			Debug.Assert(this.returnOrThrowResponse == null);

			this.returnOrThrowResponse = new ReturnEagerValueResponse(value);
		}

		public void SetReturnsResponse(Delegate valueFactory)
		{
			Debug.Assert((this.flags & Flags.MethodIsNonVoid) != 0);
			Debug.Assert(this.returnOrThrowResponse == null);

			if (valueFactory == null)
			{
				// A `null` reference (instead of a valid delegate) is interpreted as the actual return value.
				// This is necessary because the compiler might have picked the unexpected overload for calls
				// like `Returns(null)`, or the user might have picked an overload like `Returns<T>(null)`,
				// and instead of in `Returns(TResult)`, we ended up in `Returns(Delegate)` or `Returns(Func)`,
				// which likely isn't what the user intended.
				// So here we do what we would've done in `Returns(TResult)`:
				this.returnOrThrowResponse = new ReturnEagerValueResponse(this.Method.ReturnType.GetDefaultValue());
			}
			else if (this.Method.ReturnType == typeof(Delegate))
			{
				// If `TResult` is `Delegate`, that is someone is setting up the return value of a method
				// that returns a `Delegate`, then we have arrived here because C# picked the wrong overload:
				// We don't want to invoke the passed delegate to get a return value; the passed delegate
				// already is the return value.
				this.returnOrThrowResponse = new ReturnEagerValueResponse(valueFactory);
			}
			else
			{
				ValidateCallback(valueFactory);
				this.returnOrThrowResponse = new ReturnLazyValueResponse(valueFactory);
			}

			void ValidateCallback(Delegate callback)
			{
				var callbackMethod = callback.GetMethodInfo();

				// validate number of parameters:

				var numberOfActualParameters = callbackMethod.GetParameters().Length;
				if (callbackMethod.IsStatic)
				{
					if (callbackMethod.IsExtensionMethod() || callback.Target != null)
					{
						numberOfActualParameters--;
					}
				}

				if (numberOfActualParameters > 0)
				{
					var numberOfExpectedParameters = this.Method.GetParameters().Length;
					if (numberOfActualParameters != numberOfExpectedParameters)
					{
						throw new ArgumentException(
							string.Format(
								CultureInfo.CurrentCulture,
								Resources.InvalidCallbackParameterCountMismatch,
								numberOfExpectedParameters,
								numberOfActualParameters));
					}
				}

				// validate return type:

				var actualReturnType = callbackMethod.ReturnType;

				if (actualReturnType == typeof(void))
				{
					throw new ArgumentException(Resources.InvalidReturnsCallbackNotADelegateWithReturnType);
				}

				var expectedReturnType = this.Method.ReturnType;

				if (!expectedReturnType.IsAssignableFrom(actualReturnType))
				{
					throw new ArgumentException(
						string.Format(
							CultureInfo.CurrentCulture,
							Resources.InvalidCallbackReturnTypeMismatch,
							expectedReturnType,
							actualReturnType));
				}
			}
		}

		public void SetThrowExceptionResponse(Exception exception)
		{
			this.returnOrThrowResponse = new ThrowExceptionResponse(exception);
		}

		public override MockException TryVerifyAll()
		{
			return (this.flags & Flags.Invoked) == 0 ? MockException.UnmatchedSetup(this) : null;
		}

		public override void Uninvoke()
		{
			this.flags &= ~Flags.Invoked;
			this.limitInvocationCountResponse?.Reset();
		}

		public void Verifiable()
		{
			this.flags |= Flags.Verifiable;
		}

		public void Verifiable(string failMessage)
		{
			this.flags |= Flags.Verifiable;
			this.failMessage = failMessage;
		}

		public void AtMost(int count)
		{
			this.limitInvocationCountResponse = new LimitInvocationCountResponse(this, count);
		}

		public override string ToString()
		{
			var message = new StringBuilder();

			if (this.failMessage != null)
			{
				message.Append(this.failMessage).Append(": ");
			}

			message.Append(base.ToString());

#if FEATURE_CALLERINFO
			if (this.declarationSite != null)
			{
				message.Append(" (").Append(this.declarationSite).Append(')');
			}
#endif

			return message.ToString().Trim();
		}

		[Flags]
		private enum Flags : byte
		{
			CallBase = 1,
			Invoked = 2,
			MethodIsNonVoid = 4,
			Verifiable = 8,
		}

		private sealed class LimitInvocationCountResponse
		{
			private readonly MethodCall setup;
			private readonly int maxCount;
			private int count;

			public LimitInvocationCountResponse(MethodCall setup, int maxCount)
			{
				this.setup = setup;
				this.maxCount = maxCount;
				this.count = 0;
			}

			public void Reset()
			{
				this.count = 0;
			}

			public void RespondTo(Invocation invocation)
			{
				++this.count;

				if (this.count > this.maxCount)
				{
					if (this.maxCount == 1)
					{
						throw MockException.MoreThanOneCall(this.setup, this.count);
					}
					else
					{
						throw MockException.MoreThanNCalls(this.setup, this.maxCount, this.count);
					}
				}
			}
		}

		private abstract class Response
		{
			protected Response()
			{
			}

			public abstract void RespondTo(Invocation invocation);
		}

		private sealed class ReturnBaseResponse : Response
		{
			public static readonly ReturnBaseResponse Instance = new ReturnBaseResponse();

			private ReturnBaseResponse()
			{
			}

			public override void RespondTo(Invocation invocation)
			{
				invocation.ReturnBase();
			}
		}

		private sealed class ReturnEagerValueResponse : Response
		{
			public readonly object Value;

			public ReturnEagerValueResponse(object value)
			{
				this.Value = value;
			}

			public override void RespondTo(Invocation invocation)
			{
				invocation.Return(this.Value);
			}
		}

		private sealed class ReturnLazyValueResponse : Response
		{
			private readonly Delegate valueFactory;

			public ReturnLazyValueResponse(Delegate valueFactory)
			{
				this.valueFactory = valueFactory;
			}

			public override void RespondTo(Invocation invocation)
			{
				invocation.Return(this.valueFactory.CompareParameterTypesTo(Type.EmptyTypes)
					? valueFactory.InvokePreserveStack()                //we need this, for the user to be able to use parameterless methods
					: valueFactory.InvokePreserveStack(invocation.Arguments)); //will throw if parameters mismatch
			}
		}

		private sealed class ThrowExceptionResponse : Response
		{
			private readonly Exception exception;

			public ThrowExceptionResponse(Exception exception)
			{
				this.exception = exception;
			}

			public override void RespondTo(Invocation invocation)
			{
				throw this.exception;
			}
		}

		private sealed class RaiseEventResponse
		{
			private Mock mock;
			private LambdaExpression expression;
			private Delegate eventArgsFunc;
			private object[] eventArgsParams;

			public RaiseEventResponse(Mock mock, LambdaExpression expression, Delegate eventArgsFunc, object[] eventArgsParams)
			{
				Debug.Assert(mock != null);
				Debug.Assert(expression != null);
				Debug.Assert(eventArgsFunc != null ^ eventArgsParams != null);

				this.mock = mock;
				this.expression = expression;
				this.eventArgsFunc = eventArgsFunc;
				this.eventArgsParams = eventArgsParams;
			}

			public void RespondTo(Invocation invocation)
			{
				object[] args;

				if (this.eventArgsParams != null)
				{
					args = this.eventArgsParams;
				}
				else
				{
					var argsFuncType = this.eventArgsFunc.GetType();
					if (argsFuncType.IsGenericType && argsFuncType.GetGenericArguments().Length == 1)
					{
						args = new object[] { this.mock.Object, this.eventArgsFunc.InvokePreserveStack() };
					}
					else
					{
						args = new object[] { this.mock.Object, this.eventArgsFunc.InvokePreserveStack(invocation.Arguments) };
					}
				}

				Mock.RaiseEvent(this.mock, this.expression, this.expression.Split(), args);
			}
		}
	}
}
