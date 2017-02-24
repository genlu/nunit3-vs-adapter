﻿// ***********************************************************************
// Copyright (c) 2011-2015 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

//#define LAUNCHDEBUGGER

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using NUnit.Engine;

namespace NUnit.VisualStudio.TestAdapter
{
    [FileExtension(".dll")]
    [FileExtension(".exe")]
    [DefaultExecutorUri(NUnit3TestExecutor.ExecutorUri)]
    public sealed class NUnit3TestDiscoverer : NUnitTestAdapter, ITestDiscoverer
    {
        #region ITestDiscoverer Members

        public void DiscoverTests(IEnumerable<string> sources, IDiscoveryContext discoveryContext, IMessageLogger messageLogger, ITestCaseDiscoverySink discoverySink)
        {
#if LAUNCHDEBUGGER
            if (!Debugger.IsAttached)
                Debugger.Launch();
#endif
            Initialize(discoveryContext, messageLogger);

            TestLog.Info(string.Format("NUnit Adapter {0}: Test discovery starting", AdapterVersion));

            // Ensure any channels registered by other adapters are unregistered
            CleanUpRegisteredChannels();

            if (Settings.InProcDataCollectorsAvailable && sources.Count() > 1)
            {
                TestLog.Error("Unexpected to discover tests in multiple assemblies when InProcDataCollectors specified in run configuration.");
                Unload();
                return;
            }

            foreach (string sourceAssembly in sources)
            {
                TestLog.Debug("Processing " + sourceAssembly);

                // Only save if seed is not specified in runsettings
                // This allows workaround in case there is no valid
                // location in which the seed may be saved.
                if (!Settings.RandomSeedSpecified)
                    Settings.SaveRandomSeed(Path.GetDirectoryName(sourceAssembly));

                ITestRunner runner = null;

                try
                {
                    runner = GetRunnerFor(sourceAssembly);

                    XmlNode topNode = runner.Explore(TestFilter.Empty);

                    // Currently, this will always be the case but it might change
                    if (topNode.Name == "test-run")
                        topNode = topNode.FirstChild;

                    if (topNode.GetAttribute("runstate") == "Runnable")
                    {
                        var testConverter = new TestConverter(TestLog, sourceAssembly);

                        int cases = ProcessTestCases(topNode, discoverySink, testConverter);

                        TestLog.Debug(string.Format("Discovered {0} test cases", cases));
                    }
                    else
                    {
                        var msgNode = topNode.SelectSingleNode("properties/property[@name='_SKIPREASON']");
                        if (msgNode != null && (new[] { "contains no tests", "Has no TestFixtures" }).Any(msgNode.GetAttribute("value").Contains))
                            TestLog.Info("Assembly contains no NUnit 3.0 tests: " + sourceAssembly);
                        else
                            TestLog.Info("NUnit failed to load " + sourceAssembly);
                    }
                }
                catch (BadImageFormatException)
                {
                    // we skip the native c++ binaries that we don't support.
                    TestLog.Warning("Assembly not supported: " + sourceAssembly);
                }
                catch (FileNotFoundException ex)
                {
                    // Either the NUnit framework was not referenced by the test assembly
                    // or some other error occured. Not a problem if not an NUnit assembly.
                    TestLog.Warning("Dependent Assembly " + ex.FileName + " of " + sourceAssembly + " not found. Can be ignored if not a NUnit project.");
                }
                catch (FileLoadException ex)
                {
                    // Attempts to load an invalid assembly, or an assembly with missing dependencies
                    TestLog.Warning("Assembly " + ex.FileName + " loaded through " + sourceAssembly + " failed. Assembly is ignored. Correct deployment of dependencies if this is an error.");
                }
                catch (TypeLoadException ex)
                {
                    if (ex.TypeName == "NUnit.Framework.Api.FrameworkController")
                        TestLog.Warning("   Skipping NUnit 2.x test assembly");
                    else
                        TestLog.Warning("Exception thrown discovering tests in " + sourceAssembly, ex);
                }
                catch (Exception ex)
                {
                    TestLog.Warning("Exception thrown discovering tests in " + sourceAssembly, ex);
                }
                finally
                {
                    if (runner != null)
                    {
                        if (runner.IsTestRunning)
                            runner.StopRun(true);

                        runner.Unload();
                        runner.Dispose();
                    }
                }
            }

            TestLog.Info(string.Format("NUnit Adapter {0}: Test discovery complete", AdapterVersion));

            Unload();
        }

        #endregion

        #region Helper Methods

        private int ProcessTestCases(XmlNode topNode, ITestCaseDiscoverySink discoverySink, TestConverter testConverter)
        {
            int cases = 0;

            foreach (XmlNode testNode in topNode.SelectNodes("//test-case"))
            {
                try
                {
#if LAUNCHDEBUGGER
                    if (!Debugger.IsAttached)
                        Debugger.Launch();
#endif
                    TestCase testCase = testConverter.ConvertTestCase(testNode);

                    TestLog.Info(string.Format("NUnit Adapter {0}: Discovered TestCase FQN: {1}, Executor uri: {2}, Source: {3}, Id: {4}", 
                        AdapterVersion,
                        testCase.FullyQualifiedName, 
                        testCase.ExecutorUri, 
                        testCase.Source, 
                        testCase.Id));

                    discoverySink.SendTestCase(testCase);
                    cases += 1;
                }
                catch (Exception ex)
                {
                    TestLog.Warning("Exception converting " + testNode.GetAttribute("fullname"), ex);
                }
            }

            return cases;
        }

        #endregion
    }
}
