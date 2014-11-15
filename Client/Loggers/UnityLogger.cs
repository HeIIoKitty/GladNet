﻿using GladNet.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GladNet.Client
{
#if UNITYDEBUG || UNITYRELEASE
	public class UnityLogger : Logger
	{
		public UnityLogger(LogType state)
			:base(state)
		{

		}

		protected override void Log(string text, Logger.LogType state)
		{
			StringBuilder builder = new StringBuilder(state.ToString());
			builder.Append(": ").Append(text);

			switch(state)
			{
				case LogType.Debug:
					Debug.Log(builder.ToString());
					break;
				case LogType.Warn:
					Debug.LogWarning(builder.ToString());
					break;
				case LogType.Error:
					Debug.LogError(builder.ToString());
					break;
			}
		}

		protected override void Log(string text, Logger.LogType state, params object[] data)
		{
			StringBuilder builder = new StringBuilder(state.ToString());
			builder.Append(": ").AppendFormat(text, data);

			switch(state)
			{
				case LogType.Debug:
					Debug.Log(builder.ToString());
					break;
				case LogType.Warn:
					Debug.LogWarning(builder.ToString());
					break;
				case LogType.Error:
					Debug.LogError(builder.ToString());
					break;
			}
		}

		protected override void Log(string text, Logger.LogType state, params string[] data)
		{
			this.Log(text, state, (object[])data);
		}

		protected override void Log(object obj, Logger.LogType state)
		{
			this.Log((string)(obj == null ? "[NULL]" : obj.ToString()), state);
		}
	}
#endif
}
