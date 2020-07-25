using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tgstation.Server.Host.System
{
	/// <inheritdoc />
	sealed class Process : IProcess
	{
		/// <summary>
		/// Maximum time to wait in a call to <see cref="global::System.Diagnostics.Process.WaitForExit(int)"/>.
		/// </summary>
		const int MaximumWaitMilliseconds = 30000;

		/// <inheritdoc />
		public int Id { get; }

		/// <inheritdoc />
		public Task Startup { get; }

		/// <inheritdoc />
		public Task<int> Lifetime { get; }

		/// <summary>
		/// The <see cref="IProcessFeatures"/> for the <see cref="Process"/>.
		/// </summary>
		readonly IProcessFeatures processFeatures;

		/// <summary>
		/// The <see cref="ILogger"/> for the <see cref="Process"/>
		/// </summary>
		readonly ILogger<Process> logger;

		readonly global::System.Diagnostics.Process handle;

		/// <summary>
		/// A <see cref="TaskCompletionSource{TResult}"/> so that we can complete <see cref="Lifetime"/> if the <see cref="handle"/> becomes unresponsive.
		/// </summary>
		readonly TaskCompletionSource<object> emergencyLifetimeTcs;

		readonly StringBuilder outputStringBuilder;
		readonly StringBuilder errorStringBuilder;
		readonly StringBuilder combinedStringBuilder;

		/// <summary>
		/// Construct a <see cref="Process"/>
		/// </summary>
		/// <param name="processFeatures">The value of <see cref="processFeatures"/></param>
		/// <param name="handle">The value of <see cref="handle"/></param>
		/// <param name="lifetime">The value of <see cref="Lifetime"/></param>
		/// <param name="outputStringBuilder">The value of <see cref="outputStringBuilder"/></param>
		/// <param name="errorStringBuilder">The value of <see cref="errorStringBuilder"/></param>
		/// <param name="combinedStringBuilder">The value of <see cref="combinedStringBuilder"/></param>
		/// <param name="logger">The value of <see cref="logger"/></param>
		/// <param name="preExisting">If <paramref name="handle"/> was NOT just created</param>
		public Process(
			IProcessFeatures processFeatures,
			global::System.Diagnostics.Process handle,
			Task<int> lifetime,
			StringBuilder outputStringBuilder,
			StringBuilder errorStringBuilder,
			StringBuilder combinedStringBuilder,
			ILogger<Process> logger,
			bool preExisting)
		{
			this.processFeatures = processFeatures ?? throw new ArgumentNullException(nameof(processFeatures));
			this.handle = handle ?? throw new ArgumentNullException(nameof(handle));

			this.outputStringBuilder = outputStringBuilder;
			this.errorStringBuilder = errorStringBuilder;
			this.combinedStringBuilder = combinedStringBuilder;

			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

			emergencyLifetimeTcs = new TaskCompletionSource<object>();
			Lifetime = WrapLifetimeTask(lifetime ?? throw new ArgumentNullException(nameof(lifetime)));

			Id = handle.Id;

			if (preExisting)
			{
				Startup = Task.CompletedTask;
				return;
			}

			Startup = Task.Factory.StartNew(() =>
			{
				try
				{
					handle.WaitForInputIdle();
				}
				catch (InvalidOperationException) { }
			}, default, TaskCreationOptions.LongRunning, TaskScheduler.Current);

			logger.LogTrace("Created process ID: {0}", Id);
		}

		/// <inheritdoc />
		public void Dispose() => handle.Dispose();

		async Task<int> WrapLifetimeTask(Task<int> lifetimeTask)
		{
			await Task.WhenAny(lifetimeTask, emergencyLifetimeTcs.Task).ConfigureAwait(false);
			if (lifetimeTask.IsCompleted)
			{
				var exitCode = await lifetimeTask.ConfigureAwait(false);
				logger.LogTrace("PID {0} exited with code {1}", Id, exitCode);
				return exitCode;
			}

			logger.LogTrace("Using exit code -1 for hung PID {0}.", Id);
			return -1;
		}

		/// <inheritdoc />
		public string GetCombinedOutput()
		{
			if (combinedStringBuilder == null)
				throw new InvalidOperationException("Output/Error reading was not enabled!");
			return combinedStringBuilder.ToString().TrimStart(Environment.NewLine.ToCharArray());
		}

		/// <inheritdoc />
		public string GetErrorOutput()
		{
			if (errorStringBuilder == null)
				throw new InvalidOperationException("Error reading was not enabled!");
			return errorStringBuilder.ToString().TrimStart(Environment.NewLine.ToCharArray());
		}

		/// <inheritdoc />
		public string GetStandardOutput()
		{
			if (outputStringBuilder == null)
				throw new InvalidOperationException("Output reading was not enabled!");
			return outputStringBuilder.ToString().TrimStart(Environment.NewLine.ToCharArray());
		}

		/// <inheritdoc />
		public void Terminate()
		{
			if (handle.HasExited)
				return;
			try
			{
				logger.LogTrace("Terminating PID {0}...", Id);
				handle.Kill();
				if (!handle.WaitForExit(MaximumWaitMilliseconds))
				{
					logger.LogError(
						"PID {0} hasn't exited in {1} seconds! This may cause issues with port reuse.",
						Id,
						TimeSpan.FromMilliseconds(MaximumWaitMilliseconds).TotalSeconds);

					emergencyLifetimeTcs.TrySetResult(null);
				}
			}
			catch (Exception e)
			{
				logger.LogDebug(e, "Process termination exception!");
			}
		}

		/// <inheritdoc />
		public void SetHighPriority()
		{
			try
			{
				handle.PriorityClass = ProcessPriorityClass.AboveNormal;
				logger.LogTrace("Set PID {0} to above normal priority", Id);
			}
			catch (Exception e)
			{
				logger.LogWarning(e, "Unable to raise process priority for PID {0}!", Id);
			}
		}

		/// <inheritdoc />
		public void Suspend()
		{
			try
			{
				processFeatures.SuspendProcess(handle);
				logger.LogTrace("Suspended PID {0}", Id);
			}
			catch (Exception e)
			{
				logger.LogError(e, "Failed to suspend PID {0}!", Id);
				throw;
			}
		}

		/// <inheritdoc />
		public void Resume()
		{
			try
			{
				processFeatures.ResumeProcess(handle);
				logger.LogTrace("Resumed PID {0}", Id);
			}
			catch (Exception e)
			{
				logger.LogError(e, "Failed to resume PID {0}!", Id);
				throw;
			}
		}

			/// <inheritdoc />
		public async Task<string> GetExecutingUsername(CancellationToken cancellationToken)
		{
			var result = await processFeatures.GetExecutingUsername(handle, cancellationToken).ConfigureAwait(false);
			logger.LogTrace("PID {0} Username: {1}", Id, result);
			return result;
		}

		/// <inheritdoc />
		public Task CreateDump(string outputFile, CancellationToken cancellationToken)
		{
			if (outputFile == null)
				throw new ArgumentNullException(nameof(outputFile));

			logger.LogTrace("Dumping PID {0} to {1}...", Id, outputFile);
			return processFeatures.CreateDump(handle, outputFile, cancellationToken);
		}
	}
}
