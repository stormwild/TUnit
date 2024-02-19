﻿using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using TUnit.Core;
using TUnit.Core.Interfaces;
using TUnit.Engine.Extensions;
using TimeoutException = TUnit.Core.Exceptions.TimeoutException;

namespace TUnit.Engine;

internal class SingleTestExecutor
{
    private readonly MethodInvoker _methodInvoker;
    private readonly TestClassCreator _testClassCreator;
    private readonly TestMethodRetriever _testMethodRetriever;
    private readonly Disposer _disposer;
    private readonly IMessageLogger _messageLogger;
    private readonly ITestExecutionRecorder _testExecutionRecorder;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ClassWalker _classWalker;

    public SingleTestExecutor(MethodInvoker methodInvoker, 
        TestClassCreator testClassCreator,
        TestMethodRetriever testMethodRetriever,
        Disposer disposer,
        IMessageLogger messageLogger,
        ITestExecutionRecorder testExecutionRecorder,
        CancellationTokenSource cancellationTokenSource,
        ClassWalker classWalker)
    {
        _methodInvoker = methodInvoker;
        _testClassCreator = testClassCreator;
        _testMethodRetriever = testMethodRetriever;
        _disposer = disposer;
        _messageLogger = messageLogger;
        _testExecutionRecorder = testExecutionRecorder;
        _cancellationTokenSource = cancellationTokenSource;
        _classWalker = classWalker;
    }
    
    private readonly ConcurrentDictionary<string, Task> _oneTimeSetUpRegistry = new();

    public async Task<TUnitTestResult> ExecuteTest(TestCase testCase)
    {
        var result = await ExecuteInternal(testCase);
        
        _testExecutionRecorder.RecordResult(new TestResult(testCase)
        {
            DisplayName = testCase.DisplayName,
            Outcome = GetOutcome(result.TestContext, result.Status),
            ComputerName = result.ComputerName,
            Duration = result.Duration,
            StartTime = result.Start,
            EndTime = result.End,
            Messages = { new TestResultMessage("Output", result.Output) },
            ErrorMessage = result.Exception?.Message,
            ErrorStackTrace = result.Exception?.StackTrace,
        });

        return result;
    }

    private async Task<TUnitTestResult> ExecuteInternal(TestCase testCase)
    {
        var start = DateTimeOffset.Now;
        
        if (testCase.GetPropertyValue(TUnitTestProperties.IsSkipped, false))
        {
            _messageLogger.SendMessage(TestMessageLevel.Informational, $"Skipping {testCase.DisplayName}...");

            return new TUnitTestResult
            {
                Duration = TimeSpan.Zero,
                Start = start,
                End = start,
                ComputerName = Environment.MachineName,
                Exception = null,
                Status = Status.Skipped
            };
        }
        
        object? classInstance = null;
        TestContext? testContext = null;
        try
        {
            await Task.Run(async () =>
            {
                classInstance = _testClassCreator.CreateClass(testCase, out var classType);

                var methodInfo = _testMethodRetriever.GetTestMethod(classType, testCase);

                var testInformation = testCase.ToTestInformation(classType, classInstance, methodInfo);
                
                testContext = new TestContext(testInformation);
                TestContext.Current = testContext;

                var customTestAttributes = methodInfo.GetCustomAttributes()
                    .Concat(classType.GetCustomAttributes())
                    .OfType<ITestAttribute>();
                
                foreach (var customTestAttribute in customTestAttributes)
                {
                    await customTestAttribute.ApplyToTest(testContext);
                }
                
                try
                {
                    if (testContext.FailReason != null)
                    {
                        throw new Exception(testContext.FailReason);
                    }

                    if(testContext.SkipReason == null)
                    {
                        await ExecuteWithRetries(testContext, testInformation, classInstance);
                    }
                }
                finally
                {
                    await _disposer.DisposeAsync(classInstance);
                }
            });

            var end = DateTimeOffset.Now;

            return new TUnitTestResult
            {
                TestContext = testContext,
                Duration = end - start,
                Start = start,
                End = end,
                ComputerName = Environment.MachineName,
                Exception = null,
                Status = testContext!.SkipReason != null ? Status.Skipped : Status.Passed,
                Output = testContext?.GetConsoleOutput() ?? testContext!.FailReason ?? testContext.SkipReason
            };
        }
        catch (Exception e)
        {
            var end = DateTimeOffset.Now;

            var unitTestResult = new TUnitTestResult
            {
                Duration = end - start,
                Start = start,
                End = end,
                ComputerName = Environment.MachineName,
                Exception = e,
                Status = testContext?.SkipReason != null ? Status.Skipped : Status.Failed,
                Output = testContext?.GetConsoleOutput()
            };
            
            if (testContext != null)
            {
                testContext.Result = unitTestResult;
            }
            
            await ExecuteCleanUps(classInstance);
            
            return unitTestResult;
        }
    }

    private async Task ExecuteWithRetries(TestContext testContext, TestInformation testInformation, object? @class)
    {
        var retryCount = testInformation.RetryCount;
        
        // +1 for the original non-retry
        for (var i = 0; i < retryCount + 1; i++)
        {
            try
            {
                await ExecuteCore(testContext, testInformation, @class);
                break;
            }
            catch (Exception e)
            {
                if (i == retryCount 
                    || !await ShouldRetry(testInformation, e))
                {
                    throw;
                }
                
                _messageLogger.SendMessage(TestMessageLevel.Warning, $"{testInformation.TestName} failed, retrying...");
            }
        }
    }

    private static async Task<bool> ShouldRetry(TestInformation testInformation, Exception e)
    {
        var retryAttribute = testInformation.LazyRetryAttribute.Value;

        if (retryAttribute == null)
        {
            return false;
        }
        
        return await retryAttribute.ShouldRetry(testInformation, e);
    }

    private async Task ExecuteCore(TestContext testContext, TestInformation testInformation, object? @class)
    {
        testInformation.CurrentExecutionCount++;
        
        await ExecuteSetUps(@class, testInformation.ClassType);

        var testLevelCancellationTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);

        if (testInformation.Timeout != null && testInformation.Timeout.Value != default)
        {
            testLevelCancellationTokenSource.CancelAfter(testInformation.Timeout.Value);
        }

        testContext.CancellationTokenSource = testLevelCancellationTokenSource;

        try
        {
            await ExecuteTestMethodWithTimeout(testInformation, @class, testLevelCancellationTokenSource);
        }
        catch
        {
            testLevelCancellationTokenSource.Cancel();
            testLevelCancellationTokenSource.Dispose();
            throw;
        }
    }

    private async Task ExecuteTestMethodWithTimeout(TestInformation testInformation, object? @class,
        CancellationTokenSource cancellationTokenSource)
    {
        
        var methodResult = _methodInvoker.InvokeMethod(@class, testInformation.MethodInfo, BindingFlags.Default,
            testInformation.TestMethodArguments, cancellationTokenSource.Token);

        if (testInformation.Timeout == null || testInformation.Timeout.Value == default)
        {
            await methodResult;
            return;
        }
        
        var timeoutTask = Task.Delay(testInformation.Timeout.Value, cancellationTokenSource.Token)
            .ContinueWith(_ => throw new TimeoutException(testInformation));

        await await Task.WhenAny(timeoutTask, methodResult);
    }

    private async Task ExecuteSetUps(object? @class, Type testDetailsClassType)
    {
        await _oneTimeSetUpRegistry.GetOrAdd(testDetailsClassType.FullName!, _ => ExecuteOnlyOnceSetUps(@class, testDetailsClassType));

        var setUpMethods = _classWalker.GetSelfAndBaseTypes(testDetailsClassType)
            .Reverse()
            .SelectMany(x => x.GetMethods())
            .Where(x => !x.IsStatic)
            .Where(x => x.GetCustomAttributes<SetUpAttribute>().Any());

        foreach (var setUpMethod in setUpMethods)
        {
            await _methodInvoker.InvokeMethod(@class, setUpMethod, BindingFlags.Default, null, default);
        }
    }
    
    private async Task ExecuteCleanUps(object? @class)
    {
        if (@class is null)
        {
            return;
        }
        
        var cleanUpMethods = _classWalker.GetSelfAndBaseTypes(@class.GetType())
            .SelectMany(x => x.GetMethods())
            .Where(x => !x.IsStatic)
            .Where(x => x.GetCustomAttributes<CleanUpAttribute>().Any());

        var exceptions = new List<Exception>();
        
        foreach (var cleanUpMethod in cleanUpMethods)
        {
            try
            {
                await _methodInvoker.InvokeMethod(@class, cleanUpMethod, BindingFlags.Default, null, default);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Count == 1)
        {
            _messageLogger.SendMessage(TestMessageLevel.Error, $"""
                                                               Error running CleanUp:
                                                                  {exceptions.First().Message}
                                                                  {exceptions.First().StackTrace}
                                                               """);
        }
        else if (exceptions.Count > 1)
        {
            var aggregateException = new AggregateException(exceptions);
            _messageLogger.SendMessage(TestMessageLevel.Error, $"""
                                                                Error running CleanUp:
                                                                   {aggregateException.Message}
                                                                   {aggregateException.StackTrace}
                                                                """);
        }
    }

    private async Task ExecuteOnlyOnceSetUps(object? @class, Type testDetailsClassType)
    {
        var oneTimeSetUpMethods = _classWalker.GetSelfAndBaseTypes(testDetailsClassType)
            .Reverse()
            .SelectMany(x => x.GetMethods())
            .Where(x => x.IsStatic)
            .Where(x => x.GetCustomAttributes<OnlyOnceSetUpAttribute>().Any());

        foreach (var oneTimeSetUpMethod in oneTimeSetUpMethods)
        {
            await _methodInvoker.InvokeMethod(@class, oneTimeSetUpMethod, BindingFlags.Static | BindingFlags.Public, null, default);
        }
    }

    private TestOutcome GetOutcome(TestContext? resultTestContext, Status resultStatus)
    {
        if (!string.IsNullOrEmpty(resultTestContext?.FailReason))
        {
            return TestOutcome.Failed;
        }

        if (!string.IsNullOrEmpty(resultTestContext?.SkipReason))
        {
            return TestOutcome.Skipped;
        }
        
        return resultStatus switch
        {
            Status.None => TestOutcome.None,
            Status.Passed => TestOutcome.Passed,
            Status.Failed => TestOutcome.Failed,
            Status.Skipped => TestOutcome.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(resultStatus), resultStatus, null)
        };
    }
}