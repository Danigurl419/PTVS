// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.PythonTools.Common.Parsing;
using Microsoft.PythonTools.Debugger.Concord.Proxies;
using Microsoft.PythonTools.Debugger.Concord.Proxies.Structs;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Native;

namespace Microsoft.PythonTools.Debugger.Concord {
    // This class implements functionality that is logically a part of TraceManager, but has to be implemented on LocalComponent
    // and LocalStackWalkingComponent side due to DKM API location restrictions.
    internal class TraceManagerLocalHelper : DkmDataItem {
        // There's one of each - StepIn is owned by LocalComponent, StepOut is owned by LocalStackWalkingComponent.
        // See the comment on the latter for explanation on why this is necessary.
        public enum Kind { StepIn, StepOut }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct PyObject_FieldOffsets {
            public readonly long ob_type;

            public PyObject_FieldOffsets(DkmProcess process) {
                var fields = StructProxy.GetStructFields<PyObject, PyObject.PyObject_Fields>(process);
                ob_type = fields.ob_type.Offset;
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct PyVarObject_FieldOffsets {
            public readonly long ob_size;

            public PyVarObject_FieldOffsets(DkmProcess process) {
                var fields = StructProxy.GetStructFields<PyVarObject, PyVarObject.PyVarObject_Fields>(process);
                ob_size = fields.ob_size.Offset;
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct PyCodeObject_FieldOffsets {
            public readonly long co_filename, co_name;

            public PyCodeObject_FieldOffsets(DkmProcess process) {
                if (process.GetPythonRuntimeInfo().LanguageVersion <= PythonLanguageVersion.V310) {
                    var fields = StructProxy.GetStructFields<PyCodeObject310, PyCodeObject310.Fields>(process);
                    co_filename = fields.co_filename.Offset;
                    co_name = fields.co_name.Offset;
                } else {
                    var fields = StructProxy.GetStructFields<PyCodeObject311, PyCodeObject311.Fields>(process);
                    co_filename = fields.co_filename.Offset;
                    co_name = fields.co_name.Offset;
                }
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct PyFrameObject_FieldOffsets {
            public readonly long f_back, f_code, f_globals, f_locals, f_lineno, f_frame;

            public PyFrameObject_FieldOffsets(DkmProcess process) {
                // For 310, these are on the _frame struct itself.
                if (process.GetPythonRuntimeInfo().LanguageVersion <= PythonLanguageVersion.V310) {
                    var fields = StructProxy.GetStructFields<PyFrameObject310, PyFrameObject310.Fields>(process);
                    f_back = fields.f_back.Offset;
                    f_code = fields.f_code.Offset;
                    f_globals = fields.f_globals.Offset;
                    f_locals = fields.f_locals.Offset;
                    f_lineno = fields.f_lineno.Offset;
                    f_frame = 0;
                    return;
                }

                // For 311 and higher, they are on the PyInterpreterFrame struct which is pointed to by the _frame struct.
                var _frameFields = StructProxy.GetStructFields<PyFrameObject311, PyFrameObject311.Fields>(process);
                var _interpreterFields = StructProxy.GetStructFields<PyInterpreterFrame, PyInterpreterFrame.Fields>(process);
                f_frame = _frameFields.f_frame.Offset;
                f_back = _frameFields.f_back.Offset;
                f_code = _interpreterFields.f_code.Process != null ? _interpreterFields.f_code.Offset : _interpreterFields.f_executable.Offset;
                f_globals = _interpreterFields.f_globals.Offset;
                f_locals = _interpreterFields.f_locals.Offset;
                f_lineno = _frameFields.f_lineno.Offset;
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct PyBytesObject_FieldOffsets {
            public readonly long ob_sval;

            public PyBytesObject_FieldOffsets(DkmProcess process) {
                var fields = StructProxy.GetStructFields<PyBytesObject, PyBytesObject.Fields>(process);
                ob_sval = fields.ob_sval.Offset;
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct PyUnicodeObject_FieldOffsets {
            public readonly long sizeof_PyASCIIObject, sizeof_PyCompactUnicodeObject;
            public readonly long length, state, wstr, wstr_length, utf8, utf8_length, data;

            public PyUnicodeObject_FieldOffsets(DkmProcess process) {
                if (process.GetPythonRuntimeInfo().LanguageVersion <= PythonLanguageVersion.V311) {
                    sizeof_PyASCIIObject = StructProxy.SizeOf<PyASCIIObject311>(process);
                    sizeof_PyCompactUnicodeObject = StructProxy.SizeOf<PyCompactUnicodeObject311>(process);

                    var asciiFields = StructProxy.GetStructFields<PyASCIIObject311, PyASCIIObject311.Fields>(process);
                    length = asciiFields.length.Offset;
                    state = asciiFields.state.Offset;
                    wstr = asciiFields.wstr.Offset;
                    utf8 = 0;
                    utf8_length = 0;

                    var compactFields = StructProxy.GetStructFields<PyCompactUnicodeObject311, PyCompactUnicodeObject311.Fields>(process);
                    wstr_length = compactFields.wstr_length.Offset;

                    var unicodeFields = StructProxy.GetStructFields<PyUnicodeObject311, PyUnicodeObject311.Fields>(process);
                    data = unicodeFields.data.Offset;
                } else {
                    sizeof_PyASCIIObject = StructProxy.SizeOf<PyASCIIObject312>(process);
                    sizeof_PyCompactUnicodeObject = StructProxy.SizeOf<PyCompactUnicodeObject312>(process);

                    var asciiFields = StructProxy.GetStructFields<PyASCIIObject312, PyASCIIObject312.Fields>(process);
                    length = asciiFields.length.Offset;
                    state = asciiFields.state.Offset;
                    wstr = 0;

                    var compactFields = StructProxy.GetStructFields<PyCompactUnicodeObject312, PyCompactUnicodeObject312.Fields>(process);
                    wstr_length = 0;
                    utf8_length = compactFields.utf8_length.Offset;
                    utf8 = compactFields.utf8.Offset;

                    var unicodeFields = StructProxy.GetStructFields<PyUnicodeObject312, PyUnicodeObject312.Fields>(process);
                    data = unicodeFields.data.Offset;
                }

            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct FieldOffsets {
            public PyObject_FieldOffsets PyObject;
            public PyVarObject_FieldOffsets PyVarObject;
            public PyFrameObject_FieldOffsets PyFrameObject;
            public PyCodeObject_FieldOffsets PyCodeObject;
            public PyBytesObject_FieldOffsets PyBytesObject;
            public PyUnicodeObject_FieldOffsets PyUnicodeObject;

            public FieldOffsets(DkmProcess process) {
                PyObject = new PyObject_FieldOffsets(process);
                PyVarObject = new PyVarObject_FieldOffsets(process);
                PyFrameObject = new PyFrameObject_FieldOffsets(process);
                PyCodeObject = new PyCodeObject_FieldOffsets(process);
                PyBytesObject = new PyBytesObject_FieldOffsets(process);
                PyUnicodeObject = new PyUnicodeObject_FieldOffsets(process);
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct Types {
            public ulong PyBytes_Type;
            public ulong PyUnicode_Type;

            public Types(DkmProcess process, PythonRuntimeInfo pyrtInfo) {
                PyBytes_Type = PyObject.GetPyType<PyBytesObject>(process).Address;
                PyUnicode_Type = process.GetPythonRuntimeInfo().LanguageVersion <= PythonLanguageVersion.V311 ?
                    PyObject.GetPyType<PyUnicodeObject311>(process).Address :
                    PyObject.GetPyType<PyUnicodeObject312>(process).Address;
            }
        }

        // Layout of this struct must always remain in sync with DebuggerHelper/trace.cpp.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct FunctionPointers {
            public ulong Py_DecRef;
            public ulong PyFrame_FastToLocals;
            public ulong PyRun_StringFlags;
            public ulong PyErr_Fetch;
            public ulong PyErr_Restore;
            public ulong PyErr_Occurred;
            public ulong PyObject_Str;
            public ulong PyEval_SetTraceAllThreads;
            public ulong PyGILState_Ensure;
            public ulong PyGILState_Release;
            public ulong Py_Initialize;
            public ulong Py_Finalize;

            public FunctionPointers(DkmProcess process, PythonRuntimeInfo pyrtInfo) {
                Py_DecRef = pyrtInfo.DLLs.Python.GetFunctionAddress("Py_DecRef");
                PyFrame_FastToLocals = pyrtInfo.DLLs.Python.GetFunctionAddress("PyFrame_FastToLocals");
                PyRun_StringFlags = pyrtInfo.DLLs.Python.GetFunctionAddress("PyRun_StringFlags");
                PyErr_Fetch = pyrtInfo.DLLs.Python.GetFunctionAddress("PyErr_Fetch");
                PyErr_Restore = pyrtInfo.DLLs.Python.GetFunctionAddress("PyErr_Restore");
                PyErr_Occurred = pyrtInfo.DLLs.Python.GetFunctionAddress("PyErr_Occurred");
                PyObject_Str = pyrtInfo.DLLs.Python.GetFunctionAddress("PyObject_Str");
                PyEval_SetTraceAllThreads = pyrtInfo.LanguageVersion >= PythonLanguageVersion.V312 ?
                    pyrtInfo.DLLs.Python.GetFunctionAddress("PyEval_SetTraceAllThreads") :
                    0;
                PyGILState_Ensure = pyrtInfo.DLLs.Python.GetFunctionAddress("PyGILState_Ensure");
                PyGILState_Release = pyrtInfo.DLLs.Python.GetFunctionAddress("PyGILState_Release");
                Py_Initialize = pyrtInfo.DLLs.Python.GetFunctionAddress("Py_Initialize");
                Py_Finalize = pyrtInfo.DLLs.Python.GetFunctionAddress("Py_Finalize");
            }
        }

        private readonly DkmProcess _process;
        private readonly PythonRuntimeInfo _pyrtInfo;
        private readonly PythonDllBreakpointHandlers _handlers;
        private readonly DkmNativeInstructionAddress _traceFunc;
        private readonly DkmNativeInstructionAddress _evalFrameFunc;
        private readonly DkmNativeInstructionAddress _pyEval_FrameDefault;
        private readonly PointerProxy _defaultEvalFrameFunc;
        private readonly ByteProxy _isTracing;

        // A step-in gate is a function inside the Python interpreter or one of the libaries that may call out
        // to native user code such that it may be a potential target of a step-in operation. For every gate,
        // we record its address in the process, and create a breakpoint. The breakpoints are initially disabled,
        // and only get enabled when a step-in operation is initiated - and then disabled again once it completes.
        private struct StepInGate {
            public DkmRuntimeInstructionBreakpoint Breakpoint;
            public StepInGateHandler Handler;
            public bool HasMultipleExitPoints; // see StepInGateAttribute
        }

        /// <summary>
        /// A handler for a step-in gate, run either when a breakpoint at the entry of the gate is hit, or
        /// when a step-in is executed while the gate is the topmost frame on the stack. The handler should
        /// compute any potential runtime exits and pass them to <see cref="OnPotentialRuntimeExit"/>.
        /// </summary>
        /// <param name="useRegisters">
        /// If true, the handler cannot rely on symbolic expression evaluation to compute the values of any
        /// parameters passed to the gate, and should instead retrieve them directly from the CPU registers.
        /// <remarks>
        /// This is currently only true on x64 when entry breakpoint is hit, because x64 uses registers for
        /// argument passing (x86 cdecl uses the stack), and function prolog does not necessarily copy
        /// values to the corresponding stack locations for them - so C++ expression evaluator will produce
        /// incorrect results for arguments, or fail to evaluate them altogether.
        /// </remarks>
        /// </param>
        public delegate void StepInGateHandler(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters);

        private readonly List<StepInGate> _stepInGates = new List<StepInGate>();

        // Breakpoints corresponding to the native functions outside of Python runtime that can potentially
        // be called by Python. These lists are dynamically filled for every new step operation, when one of 
        // the Python DLL breakpoints above is hit. They are cleared after that step operation completes.
        private readonly List<DkmRuntimeBreakpoint> _stepInTargetBreakpoints = new List<DkmRuntimeBreakpoint>();
        private readonly List<DkmRuntimeBreakpoint> _stepOutTargetBreakpoints = new List<DkmRuntimeBreakpoint>();

        public unsafe TraceManagerLocalHelper(DkmProcess process, Kind kind) {
            _process = process;
            _pyrtInfo = process.GetPythonRuntimeInfo();

            _pyEval_FrameDefault = _pyrtInfo.DLLs.Python.GetExportedFunctionAddress("_PyEval_EvalFrameDefault");
            _traceFunc = _pyrtInfo.DLLs.DebuggerHelper.GetExportedFunctionAddress("TraceFunc");
            _evalFrameFunc = 
                _pyrtInfo.DLLs.DebuggerHelper.GetExportedFunctionAddress("EvalFrameFunc");
            _defaultEvalFrameFunc =
                _pyrtInfo.DLLs.DebuggerHelper.GetExportedStaticVariable<PointerProxy>("DefaultEvalFrameFunc");
            _isTracing = _pyrtInfo.DLLs.DebuggerHelper.GetExportedStaticVariable<ByteProxy>("isTracing");

            if (kind == Kind.StepIn) {
                var fieldOffsets = _pyrtInfo.DLLs.DebuggerHelper.GetExportedStaticVariable<CliStructProxy<FieldOffsets>>("fieldOffsets");
                fieldOffsets.Write(new FieldOffsets(process));

                var types = _pyrtInfo.DLLs.DebuggerHelper.GetExportedStaticVariable<CliStructProxy<Types>>("types");
                types.Write(new Types(process, _pyrtInfo));

                var functionPointers = _pyrtInfo.DLLs.DebuggerHelper.GetExportedStaticVariable<CliStructProxy<FunctionPointers>>("functionPointers");
                functionPointers.Write(new FunctionPointers(process, _pyrtInfo));

                foreach (var interp in PyInterpreterState.GetInterpreterStates(process)) {
                    if (_pyrtInfo.LanguageVersion >= PythonLanguageVersion.V36) {
                        RegisterJITTracing(interp);
                    }
                    foreach (var tstate in interp.GetThreadStates(process)) {
                        RegisterTracing(tstate);
                    }
                }

                _handlers = new PythonDllBreakpointHandlers(this);
                LocalComponent.CreateRuntimeDllFunctionExitBreakpoints(_pyrtInfo.DLLs.Python, "new_threadstate", _handlers.new_threadstate, enable: true);
                LocalComponent.CreateRuntimeDllFunctionExitBreakpoints(_pyrtInfo.DLLs.Python, "PyInterpreterState_New", _handlers.PyInterpreterState_New, enable: true);

                // For 3.13 we need a different function. PyInterpreterState_New is still present, but it's not the one that's callled internally,
                if (_pyrtInfo.LanguageVersion >= PythonLanguageVersion.V313) {
                    LocalComponent.CreateRuntimeDllFunctionExitBreakpoints(_pyrtInfo.DLLs.Python, "_PyInterpreterState_New", _handlers._PyInterpreterState_New, enable: true);
                }

                foreach (var methodInfo in _handlers.GetType().GetMethods()) {
                    var stepInAttr = (StepInGateAttribute)Attribute.GetCustomAttribute(methodInfo, typeof(StepInGateAttribute));
                    if (stepInAttr != null &&
                        (stepInAttr.MinVersion == PythonLanguageVersion.None || _pyrtInfo.LanguageVersion >= stepInAttr.MinVersion) &&
                        (stepInAttr.MaxVersion == PythonLanguageVersion.None || _pyrtInfo.LanguageVersion <= stepInAttr.MaxVersion)) {

                        var handler = (StepInGateHandler)Delegate.CreateDelegate(typeof(StepInGateHandler), _handlers, methodInfo);
                        AddStepInGate(handler, _pyrtInfo.DLLs.Python, methodInfo.Name, stepInAttr.HasMultipleExitPoints);
                    }
                }

                if (_pyrtInfo.DLLs.CTypes != null) {
                    OnCTypesLoaded(_pyrtInfo.DLLs.CTypes);
                }
            }
        }

        private IEnumerable<Int32Proxy> GetTracingPossible(PythonRuntimeInfo pyrtInfo, DkmProcess process) {
            // On 3.10 and above the tracing_possible is determined on each thread by the cframe object, so we don't need to
            // check the interpreter state (it's no longer set there).
            if (pyrtInfo.LanguageVersion > PythonLanguageVersion.V39) {
                return Enumerable.Empty<Int32Proxy>();
            }
            return from interp in PyInterpreterState.GetInterpreterStates(process)
            select interp.ceval.tracing_possible;
        }

        private void AddStepInGate(StepInGateHandler handler, DkmNativeModuleInstance module, string funcName, bool hasMultipleExitPoints) {
            var gate = new StepInGate {
                Handler = handler,
                HasMultipleExitPoints = hasMultipleExitPoints,
                Breakpoint = LocalComponent.CreateRuntimeDllFunctionBreakpoint(module, funcName,
                    (thread, frameBase, vframe, retAddr) => handler(thread, frameBase, vframe, useRegisters: thread.Process.Is64Bit()))
            };
            _stepInGates.Add(gate);
        }

        public void OnCTypesLoaded(DkmNativeModuleInstance moduleInstance) {
            AddStepInGate(_handlers._call_function_pointer, moduleInstance, "_call_function_pointer", hasMultipleExitPoints: false);
        }

        public unsafe void RegisterTracing(PyThreadState tstate) {
            tstate.RegisterTracing(_traceFunc.GetPointer());
            foreach (var pyTracingPossible in GetTracingPossible(_pyrtInfo, _process)) {
                pyTracingPossible.Write(pyTracingPossible.Read() + 1);
            }
            _isTracing.Write(1);
        }

        public unsafe void RegisterJITTracing(PyInterpreterState istate) {
            Debug.Assert(_pyrtInfo.LanguageVersion >= PythonLanguageVersion.V36);

            var current = istate.eval_frame.Read();
            var evalFrameAddr = _evalFrameFunc.GetPointer();

            if (current == 0) {
                // This means the eval_frame is set to the default. Write
                // this as our _defaultEvalFrameFunc
                _defaultEvalFrameFunc.Write(_pyEval_FrameDefault.GetPointer());
                istate.eval_frame.Write(evalFrameAddr);
            } else if (current != evalFrameAddr) {
                _defaultEvalFrameFunc.Write(current);
                istate.eval_frame.Write(evalFrameAddr);
            }
        }

        public void OnBeginStepIn(DkmThread thread) {
            var frameInfo = new RemoteComponent.GetCurrentFrameInfoRequest { ThreadId = thread.UniqueId }.SendLower(thread.Process);

            var workList = DkmWorkList.Create(null);
            var topFrame = thread.GetTopStackFrame();
            var curAddr = (topFrame != null) ? topFrame.InstructionAddress as DkmNativeInstructionAddress : null;

            foreach (var gate in _stepInGates) {
                gate.Breakpoint.Enable();

                // A step-in may happen when we are stopped inside a step-in gate function. For example, when the gate function
                // calls out to user code more than once, and the user then steps out from the first call; we're now inside the
                // gate, but the runtime exit breakpoints for that gate have been cleared after the previous step-in completed. 
                // To correctly handle this scenario, we need to check whether we're inside a gate with multiple exit points, and
                // if so, call the associated gate handler (as it the entry breakpoint for the gate is hit) so that it re-enables
                // the runtime exit breakpoints for that gate.
                if (gate.HasMultipleExitPoints && curAddr != null) {
                    var addr = (DkmNativeInstructionAddress)gate.Breakpoint.InstructionAddress;
                    if (addr.IsInSameFunction(curAddr)) {
                        gate.Handler(thread, frameInfo.FrameBase, frameInfo.VFrame, useRegisters: false);
                    }
                }
            }
        }

        public void OnBeginStepOut(DkmThread thread) {
            // When we're stepping out while in Python code, there are two possibilities. Either the stack looks like this:
            //
            //   PythonFrame1
            //   PythonFrame2
            //
            // or else it looks like this:
            //
            //   PythonFrame
            //   [Native to Python transition]
            //   NativeFrame
            //
            // In both cases, we use native breakpoints on the return address to catch the end of step-out operation.
            // For Python-to-native step-out, this is the only option. For Python-to-Python, it would seem that TraceFunc
            // can detect it via PyTrace_RETURN, but it doesn't actually know whether the return is to Python or to
            // native at the point where it's reported - and, in any case, we need to let PyEval_EvalFrameEx to return
            // before reporting the completion of that step-out (otherwise we will show the returning frame in call stack).

            // Find the destination for step-out by walking the call stack and finding either the first native frame
            // outside of Python and helper DLLs, or the second Python frame.
            var inspectionSession = DkmInspectionSession.Create(_process, null);
            var frameFormatOptions = new DkmFrameFormatOptions(DkmVariableInfoFlags.None, DkmFrameNameFormatOptions.None, DkmEvaluationFlags.None, 10000, 10);
            var stackContext = DkmStackContext.Create(inspectionSession, thread, DkmCallStackFilterOptions.None, frameFormatOptions, null, null);
            DkmStackFrame frame = null;
            for (int pyFrameCount = 0; pyFrameCount != 2; ) {
                DkmStackFrame[] frames = null;
                var workList = DkmWorkList.Create(null);
                stackContext.GetNextFrames(workList, 1, (result) => { frames = result.Frames; });
                workList.Execute();
                if (frames == null || frames.Length != 1) {
                    return;
                }
                frame = frames[0];

                var frameModuleInstance = frame.ModuleInstance;
                if (frameModuleInstance is DkmNativeModuleInstance &&
                    frameModuleInstance != _pyrtInfo.DLLs.Python &&
                    frameModuleInstance != _pyrtInfo.DLLs.DebuggerHelper &&
                    frameModuleInstance != _pyrtInfo.DLLs.CTypes) {
                    break;
                } else if (frame.RuntimeInstance != null && frame.RuntimeInstance.Id.RuntimeType == Guids.PythonRuntimeTypeGuid) {
                    ++pyFrameCount;
                }
            }

            var nativeAddr = frame.InstructionAddress as DkmNativeInstructionAddress;
            if (nativeAddr == null) {
                var customAddr = frame.InstructionAddress as DkmCustomInstructionAddress;
                if (customAddr == null) {
                    return;
                }

                var loc = new SourceLocation(customAddr.AdditionalData, thread.Process);
                nativeAddr = loc.NativeAddress;
                if (nativeAddr == null) {
                    return;
                }
            }

            var bp = DkmRuntimeInstructionBreakpoint.Create(Guids.PythonStepTargetSourceGuid, thread, nativeAddr, false, null);
            bp.Enable();

            _stepOutTargetBreakpoints.Add(bp);
        }

        public void OnStepComplete() {
            foreach (var gate in _stepInGates) {
                gate.Breakpoint.Disable();
            }

            foreach (var bp in _stepInTargetBreakpoints) {
                bp.Close();
            }
            _stepInTargetBreakpoints.Clear();

            foreach (var bp in _stepOutTargetBreakpoints) {
                bp.Close();
            }
            _stepOutTargetBreakpoints.Clear();
        }

        // Sets a breakpoint on a given function pointer, that represents some code outside of the Python DLL that can potentially
        // be invoked as a result of the current step-in operation (in which case it is the step-in target).
        private void OnPotentialRuntimeExit(DkmThread thread, ulong funcPtr) {
            if (funcPtr == 0) {
                return;
            }

            if (_pyrtInfo.DLLs.Python.ContainsAddress(funcPtr)) {
                return;
            } else if (_pyrtInfo.DLLs.DebuggerHelper != null && _pyrtInfo.DLLs.DebuggerHelper.ContainsAddress(funcPtr)) {
                return;
            } else if (_pyrtInfo.DLLs.CTypes != null && _pyrtInfo.DLLs.CTypes.ContainsAddress(funcPtr)) {
                return;
            }

            var bp = _process.CreateBreakpoint(Guids.PythonStepTargetSourceGuid, funcPtr);
            bp.Enable();

            _stepInTargetBreakpoints.Add(bp);
        }

        // Indicates that the breakpoint handler is for a Python-to-native step-in gate.
        [AttributeUsage(AttributeTargets.Method)]
        private class StepInGateAttribute : Attribute {
            public PythonLanguageVersion MinVersion { get; set; }
            public PythonLanguageVersion MaxVersion { get; set; }

            /// <summary>
            /// If true, this step-in gate function has more than one runtime exit point that can be executed in
            /// a single pass through the body of the function. For example, creating an instance of an object is
            /// a single gate that invokes both tp_new and tp_init sequentially.
            /// </summary>
            public bool HasMultipleExitPoints { get; set; }
        }

        private class PythonDllBreakpointHandlers {
            private readonly TraceManagerLocalHelper _owner;

            public PythonDllBreakpointHandlers(TraceManagerLocalHelper owner) {
                _owner = owner;
            }

            public void new_threadstate(DkmThread thread, ulong frameBase, ulong vframe, ulong returnAddress) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                // Addressing this local by name does not work for release builds, so read the return value directly from the register instead.
                var tstate = PyThreadState.TryCreate(process, cppEval.EvaluateReturnValueUInt64());
                if (tstate == null) {
                    return;
                }

                _owner.RegisterTracing(tstate);
            }

            public void PyInterpreterState_New(DkmThread thread, ulong frameBase, ulong vframe, ulong returnAddress) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                var istate = PyInterpreterState.TryCreate(process, cppEval.EvaluateReturnValueUInt64());
                if (istate == null) {
                    return;
                }

                if (process.GetPythonRuntimeInfo().LanguageVersion >= PythonLanguageVersion.V36) {
                    _owner.RegisterJITTracing(istate);
                }
            }

            public void _PyInterpreterState_New(DkmThread thread, ulong frameBase, ulong vframe, ulong returnAddress) {
                // The new interpreter should be the 'head' of the list of interpreters
                // (this function actually returns a status code, not the interpreter state).
                var head = PyInterpreterState.GetInterpreterStates(thread.Process).First();
                if (head != null && head.Process != null) {
                    _owner.RegisterJITTracing(head);
                }
            }

            // This step-in gate is not marked [StepInGate] because it doesn't live in pythonXX.dll, and so we register it manually.
            public void _call_function_pointer(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);
                ulong pProc = cppEval.EvaluateUInt64(useRegisters ? "@rdx" : "pProc");
                _owner.OnPotentialRuntimeExit(thread, pProc);
            }

            [StepInGate(MaxVersion = PythonLanguageVersion.V310)] 
            public void call_function(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                int oparg = cppEval.EvaluateInt32(useRegisters ? "@rdx" : "oparg");

                int na = oparg & 0xff;
                int nk = (oparg >> 8) & 0xff;
                int n = na + 2 * nk;

                ulong func = cppEval.EvaluateUInt64(
                    "*((*(PyObject***){0}) - {1} - 1)",
                    useRegisters ? "@rcx" : "pp_stack",
                    n);
                var obj = PyObject.FromAddress(process, func);
                ulong ml_meth = cppEval.EvaluateUInt64(
                    "((PyObject*){0})->ob_type == &PyCFunction_Type ? ((PyCFunctionObject*){0})->m_ml->ml_meth : 0",
                    func);

                _owner.OnPotentialRuntimeExit(thread, ml_meth);
            }

            [StepInGate(MinVersion = PythonLanguageVersion.V311)]
            public void PyObject_Vectorcall(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);
                ulong func = cppEval.EvaluateUInt64(useRegisters ? "@rdx" : "callable");
                var obj = PyObject.FromAddress(process, func);
                ulong ml_meth = cppEval.EvaluateUInt64(
                    "((PyObject*){0})->ob_type == &PyCFunction_Type ? ((PyCFunctionObject*){0})->m_ml->ml_meth : 0",
                    func);

                _owner.OnPotentialRuntimeExit(thread, ml_meth);
            }

            [StepInGate]
            public void PyCFunction_Call(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                ulong ml_meth = cppEval.EvaluateUInt64(
                    "((PyObject*){0})->ob_type == &PyCFunction_Type ? ((PyCFunctionObject*){0})->m_ml->ml_meth : 0",
                    useRegisters ? "@rcx" : "func");
                _owner.OnPotentialRuntimeExit(thread, ml_meth);
            }

            [StepInGate]
            public void getset_get(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string descrVar = useRegisters ? "((PyGetSetDescrObject*)@rcx)" : "descr";

                ulong get = cppEval.EvaluateUInt64(descrVar + "->d_getset->get");
                _owner.OnPotentialRuntimeExit(thread, get);
            }

            [StepInGate]
            public void getset_set(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string descrVar = useRegisters ? "((PyGetSetDescrObject*)@rcx)" : "descr";

                ulong set = cppEval.EvaluateUInt64(descrVar + "->d_getset->set");
                _owner.OnPotentialRuntimeExit(thread, set);
            }

            [StepInGate(HasMultipleExitPoints = true)]
            public void type_call(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string typeVar = useRegisters ? "((PyTypeObject*)@rcx)" : "type";

                ulong tp_new = cppEval.EvaluateUInt64(typeVar + "->tp_new");
                _owner.OnPotentialRuntimeExit(thread, tp_new);

                ulong tp_init = cppEval.EvaluateUInt64(typeVar + "->tp_init");
                _owner.OnPotentialRuntimeExit(thread, tp_init);
            }

            [StepInGate]
            public void PyType_GenericNew(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string typeVar = useRegisters ? "((PyTypeObject*)@rcx)" : "type";

                ulong tp_alloc = cppEval.EvaluateUInt64(typeVar + "->tp_alloc");
                _owner.OnPotentialRuntimeExit(thread, tp_alloc);
            }

            [StepInGate]
            public void PyObject_Print(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string opVar = useRegisters ? "((PyObject*)@rcx)" : "op";

                ulong tp_print = cppEval.EvaluateUInt64(opVar + "->ob_type->tp_print");
                _owner.OnPotentialRuntimeExit(thread, tp_print);
            }

            [StepInGate]
            public void PyObject_GetAttrString(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_getattr = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_getattr");
                _owner.OnPotentialRuntimeExit(thread, tp_getattr);
            }

            [StepInGate]
            public void PyObject_SetAttrString(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_setattr = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_setattr");
                _owner.OnPotentialRuntimeExit(thread, tp_setattr);
            }

            [StepInGate]
            public void PyObject_GetAttr(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_getattr = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_getattr");
                _owner.OnPotentialRuntimeExit(thread, tp_getattr);

                ulong tp_getattro = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_getattro");
                _owner.OnPotentialRuntimeExit(thread, tp_getattro);
            }

            [StepInGate]
            public void PyObject_SetAttr(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_setattr = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_setattr");
                _owner.OnPotentialRuntimeExit(thread, tp_setattr);

                ulong tp_setattro = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_setattro");
                _owner.OnPotentialRuntimeExit(thread, tp_setattro);
            }

            [StepInGate]
            public void PyObject_Repr(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_repr = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_repr");
                _owner.OnPotentialRuntimeExit(thread, tp_repr);
            }

            [StepInGate]
            public void PyObject_Hash(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_hash = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_hash");
                _owner.OnPotentialRuntimeExit(thread, tp_hash);
            }

            [StepInGate]
            public void PyObject_Call(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string funcVar = useRegisters ? "((PyObject*)@rcx)" : "func";

                ulong tp_call = cppEval.EvaluateUInt64(funcVar + "->ob_type->tp_call");
                _owner.OnPotentialRuntimeExit(thread, tp_call);
            }

            [StepInGate]
            public void PyObject_Str(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";

                ulong tp_str = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_str");
                _owner.OnPotentialRuntimeExit(thread, tp_str);
            }

            [StepInGate(MaxVersion = PythonLanguageVersion.V27, HasMultipleExitPoints = true)]
            public void do_cmp(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";
                string wVar = useRegisters ? "((PyObject*)@rdx)" : "w";

                ulong tp_compare1 = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_compare");
                _owner.OnPotentialRuntimeExit(thread, tp_compare1);

                ulong tp_richcompare1 = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_richcompare");
                _owner.OnPotentialRuntimeExit(thread, tp_richcompare1);

                ulong tp_compare2 = cppEval.EvaluateUInt64(wVar + "->ob_type->tp_compare");
                _owner.OnPotentialRuntimeExit(thread, tp_compare2);

                ulong tp_richcompare2 = cppEval.EvaluateUInt64(wVar + "->ob_type->tp_richcompare");
                _owner.OnPotentialRuntimeExit(thread, tp_richcompare2);
            }

            [StepInGate(MaxVersion = PythonLanguageVersion.V27, HasMultipleExitPoints = true)]
            public void PyObject_RichCompare(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";
                string wVar = useRegisters ? "((PyObject*)@rdx)" : "w";

                ulong tp_compare1 = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_compare");
                _owner.OnPotentialRuntimeExit(thread, tp_compare1);

                ulong tp_richcompare1 = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_richcompare");
                _owner.OnPotentialRuntimeExit(thread, tp_richcompare1);

                ulong tp_compare2 = cppEval.EvaluateUInt64(wVar + "->ob_type->tp_compare");
                _owner.OnPotentialRuntimeExit(thread, tp_compare2);

                ulong tp_richcompare2 = cppEval.EvaluateUInt64(wVar + "->ob_type->tp_richcompare");
                _owner.OnPotentialRuntimeExit(thread, tp_richcompare2);
            }

            [StepInGate(MinVersion = PythonLanguageVersion.V33, HasMultipleExitPoints = true)]
            public void do_richcompare(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string vVar = useRegisters ? "((PyObject*)@rcx)" : "v";
                string wVar = useRegisters ? "((PyObject*)@rdx)" : "w";

                ulong tp_richcompare1 = cppEval.EvaluateUInt64(vVar + "->ob_type->tp_richcompare");
                _owner.OnPotentialRuntimeExit(thread, tp_richcompare1);

                ulong tp_richcompare2 = cppEval.EvaluateUInt64(wVar + "->ob_type->tp_richcompare");
                _owner.OnPotentialRuntimeExit(thread, tp_richcompare2);
            }

            [StepInGate]
            public void PyObject_GetIter(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string oVar = useRegisters ? "((PyObject*)@rcx)" : "o";

                ulong tp_iter = cppEval.EvaluateUInt64(oVar + "->ob_type->tp_iter");
                _owner.OnPotentialRuntimeExit(thread, tp_iter);
            }

            [StepInGate]
            public void PyIter_Next(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string iterVar = useRegisters ? "((PyObject*)@rcx)" : "iter";

                ulong tp_iternext = cppEval.EvaluateUInt64(iterVar + "->ob_type->tp_iternext");
                _owner.OnPotentialRuntimeExit(thread, tp_iternext);
            }

            [StepInGate]
            public void builtin_next(DkmThread thread, ulong frameBase, ulong vframe, bool useRegisters) {
                var process = thread.Process;
                var cppEval = new CppExpressionEvaluator(thread, frameBase, vframe);

                string argsVar = useRegisters ? "((PyTupleObject*)@rdx)" : "((PyTupleObject*)args)";

                ulong tp_iternext = cppEval.EvaluateUInt64(argsVar + "->ob_item[0]->ob_type->tp_iternext");
                _owner.OnPotentialRuntimeExit(thread, tp_iternext);
            }
        }
    }
}
