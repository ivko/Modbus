using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Modbus.Ui.Automation;

public enum DispKind { Method, Property, IndexedProperty }

/// <summary>One late-bound member: a method, a property, or a property with one index argument.</summary>
public sealed class DispMember
{
    public required string Name { get; init; }
    public required int DispId { get; init; }
    public DispKind Kind { get; init; }
    public int MinArgs { get; init; }
    public int MaxArgs { get; init; }
    public bool ReturnsValue { get; init; } = true;

    /// <summary>Method call, property get (no args) or indexed get (index).</summary>
    public Func<object?[], object?>? Get { get; init; }

    /// <summary>Property put: (indexArgs, value).</summary>
    public Action<object?[], object?>? Set { get; init; }
}

/// <summary>A managed object exposed to COM clients through IDispatch.</summary>
public abstract class DispatchTarget
{
    private Dictionary<string, DispMember>? _byName;
    private Dictionary<int, DispMember>? _byId;

    protected abstract IEnumerable<DispMember> CreateMembers();

    private void EnsureMaps()
    {
        if (_byName is not null) return;
        var members = CreateMembers().ToList();
        _byName = members.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        _byId = members.ToDictionary(m => m.DispId);
    }

    public DispMember? Find(string name)
    {
        EnsureMaps();
        return _byName!.GetValueOrDefault(name);
    }

    public DispMember? Find(int dispId)
    {
        EnsureMaps();
        return _byId!.GetValueOrDefault(dispId);
    }

    /// <summary>Called when the last COM reference is released.</summary>
    public virtual void OnFinalRelease() { }

    // Helpers for argument conversion (VBA passes Integer as VT_I2, Python passes VT_I4, etc.).
    protected static int I(object? v) => v switch
    {
        null => 0,
        bool b => b ? -1 : 0,
        string s => (int)Core.Protocol.Hex.ParseNumber(s),
        _ => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    protected static double D(object? v) => v switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        string s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    protected static bool B(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
        _ => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) != 0,
    };

    protected static string S(object? v) => v?.ToString() ?? "";
}

/// <summary>Raised from a member to report a COM error with a description.</summary>
public sealed class AutomationException(string message) : Exception(message);

/// <summary>Hand-written COM objects: IUnknown + IDispatch, and IUnknown + IClassFactory.</summary>
public static unsafe class NativeCom
{
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    private const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    private const int DISP_E_BADINDEX = unchecked((int)0x8002000B);
    private const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);
    private const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);

    private const ushort DISPATCH_METHOD = 1;
    private const ushort DISPATCH_PROPERTYGET = 2;
    private const ushort DISPATCH_PROPERTYPUT = 4;
    private const ushort DISPATCH_PROPERTYPUTREF = 8;
    private const int DISPID_PROPERTYPUT = -3;

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");

    private static readonly int VariantSize = IntPtr.Size == 8 ? 24 : 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct ComObject
    {
        public void** Vtbl;
        public int RefCount;
        public IntPtr Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public IntPtr Args;
        public int* NamedArgs;
        public int ArgCount;
        public int NamedArgCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExcepInfo
    {
        public ushort Code;
        public ushort Reserved;
        public IntPtr Source;
        public IntPtr Description;
        public IntPtr HelpFile;
        public uint HelpContext;
        public IntPtr PvReserved;
        public IntPtr DeferredFillIn;
        public int Scode;
    }

    private static readonly void** DispatchVtbl;
    private static readonly void** FactoryVtbl;
    private static int _liveObjects;
    private static int _locks;

    /// <summary>Raised (on the calling COM thread) when no objects or locks remain.</summary>
    public static event Action? AllReleased;

    public static int LiveObjects => Volatile.Read(ref _liveObjects) + Volatile.Read(ref _locks);

    static NativeCom()
    {
        DispatchVtbl = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        DispatchVtbl[0] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, void**, int>)&QueryInterfaceDispatch;
        DispatchVtbl[1] = (delegate* unmanaged[Stdcall]<ComObject*, uint>)&AddRef;
        DispatchVtbl[2] = (delegate* unmanaged[Stdcall]<ComObject*, uint>)&ReleaseThunk;
        DispatchVtbl[3] = (delegate* unmanaged[Stdcall]<ComObject*, uint*, int>)&GetTypeInfoCount;
        DispatchVtbl[4] = (delegate* unmanaged[Stdcall]<ComObject*, uint, uint, IntPtr*, int>)&GetTypeInfo;
        DispatchVtbl[5] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, char**, uint, uint, int*, int>)&GetIDsOfNames;
        DispatchVtbl[6] = (delegate* unmanaged[Stdcall]<ComObject*, int, Guid*, uint, ushort, DispParams*, IntPtr, ExcepInfo*, uint*, int>)&Invoke;

        FactoryVtbl = (void**)NativeMemory.Alloc(5, (nuint)sizeof(void*));
        FactoryVtbl[0] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, void**, int>)&QueryInterfaceFactory;
        FactoryVtbl[1] = (delegate* unmanaged[Stdcall]<ComObject*, uint>)&AddRef;
        FactoryVtbl[2] = (delegate* unmanaged[Stdcall]<ComObject*, uint>)&ReleaseThunk;
        FactoryVtbl[3] = (delegate* unmanaged[Stdcall]<ComObject*, IntPtr, Guid*, void**, int>)&CreateInstance;
        FactoryVtbl[4] = (delegate* unmanaged[Stdcall]<ComObject*, int, int>)&LockServer;
    }

    public static IntPtr CreateDispatch(DispatchTarget target)
    {
        Interlocked.Increment(ref _liveObjects);
        return Allocate(DispatchVtbl, target);
    }

    public static IntPtr CreateFactory(Func<DispatchTarget> create) => Allocate(FactoryVtbl, create);

    private static IntPtr Allocate(void** vtbl, object target)
    {
        var p = (ComObject*)NativeMemory.AllocZeroed((nuint)sizeof(ComObject));
        p->Vtbl = vtbl;
        p->RefCount = 1;
        p->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(target));
        return (IntPtr)p;
    }

    public static void ReleasePointer(IntPtr p)
    {
        if (p != IntPtr.Zero) Release((ComObject*)p);
    }

    private static object? Target(ComObject* self) => GCHandle.FromIntPtr(self->Handle).Target;

    // ---- IUnknown ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterfaceDispatch(ComObject* self, Guid* riid, void** ppv)
    {
        if (ppv is null) return E_POINTER;
        if (*riid == IID_IUnknown || *riid == IID_IDispatch)
        {
            Interlocked.Increment(ref self->RefCount);
            *ppv = self;
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterfaceFactory(ComObject* self, Guid* riid, void** ppv)
    {
        if (ppv is null) return E_POINTER;
        if (*riid == IID_IUnknown || *riid == IID_IClassFactory)
        {
            Interlocked.Increment(ref self->RefCount);
            *ppv = self;
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(ComObject* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseThunk(ComObject* self) => Release(self);

    private static uint Release(ComObject* self)
    {
        var count = Interlocked.Decrement(ref self->RefCount);
        if (count != 0) return (uint)count;

        var isDispatch = self->Vtbl == DispatchVtbl;
        var handle = GCHandle.FromIntPtr(self->Handle);
        var target = handle.Target;
        handle.Free();
        NativeMemory.Free(self);
        if (isDispatch)
        {
            try { (target as DispatchTarget)?.OnFinalRelease(); }
            catch (Exception) { /* never let exceptions cross the COM boundary */ }
            if (Interlocked.Decrement(ref _liveObjects) == 0 && Volatile.Read(ref _locks) == 0) AllReleased?.Invoke();
        }
        return 0;
    }

    // ---- IDispatch ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoCount(ComObject* self, uint* count)
    {
        if (count is null) return E_POINTER;
        *count = 0;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfo(ComObject* self, uint index, uint lcid, IntPtr* typeInfo)
    {
        if (typeInfo is not null) *typeInfo = IntPtr.Zero;
        return DISP_E_BADINDEX;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIDsOfNames(ComObject* self, Guid* riid, char** names, uint count, uint lcid, int* ids)
    {
        try
        {
            if (Target(self) is not DispatchTarget target || count == 0) return DISP_E_UNKNOWNNAME;
            var name = new string(names[0]);
            var member = target.Find(name);
            ids[0] = member?.DispId ?? -1;
            for (var i = 1; i < count; i++) ids[i] = -1; // named arguments are not supported
            return member is null || count > 1 ? DISP_E_UNKNOWNNAME : S_OK;
        }
        catch (Exception)
        {
            return E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(ComObject* self, int dispId, Guid* riid, uint lcid, ushort flags, DispParams* parameters,
        IntPtr result, ExcepInfo* excepInfo, uint* argError)
    {
        try
        {
            if (Target(self) is not DispatchTarget target) return E_FAIL;
            var member = target.Find(dispId);
            if (member is null) return DISP_E_MEMBERNOTFOUND;

            // Arguments arrive in reverse order; a property put passes the value as the named argument DISPID_PROPERTYPUT.
            var count = parameters is null ? 0 : parameters->ArgCount;
            var named = parameters is null ? 0 : parameters->NamedArgCount;
            var all = new object?[count];
            for (var i = 0; i < count; i++)
                all[count - 1 - i] = Marshal.GetObjectForNativeVariant(parameters->Args + i * VariantSize);

            var isPut = (flags & (DISPATCH_PROPERTYPUT | DISPATCH_PROPERTYPUTREF)) != 0;
            object? value = null;
            object?[] args = all;
            if (isPut)
            {
                if (count == 0) return DISP_E_BADPARAMCOUNT;
                if (named == 1 && parameters->NamedArgs[0] != DISPID_PROPERTYPUT) return DISP_E_BADPARAMCOUNT;
                value = all[^1];
                args = all[..^1];
            }

            object? returned = null;
            switch (member.Kind)
            {
                case DispKind.Method:
                    if (isPut) return DISP_E_MEMBERNOTFOUND;
                    // A pure property-get on a method tells late-binding clients (pywin32) that the name is a
                    // method, so "doc.ReadResult()" works as it does with the Modbus Poll type library.
                    if (flags == DISPATCH_PROPERTYGET) return DISP_E_MEMBERNOTFOUND;
                    if (args.Length < member.MinArgs || args.Length > member.MaxArgs) return DISP_E_BADPARAMCOUNT;
                    returned = member.Get!(args);
                    break;

                case DispKind.Property:
                    if (isPut || (args.Length == 1 && (flags & DISPATCH_METHOD) != 0))
                    {
                        if (member.Set is null) return DISP_E_MEMBERNOTFOUND;
                        member.Set([], isPut ? value : args[0]);
                    }
                    else if (args.Length == 0)
                    {
                        returned = member.Get!([]);
                    }
                    else
                    {
                        return DISP_E_BADPARAMCOUNT;
                    }
                    break;

                case DispKind.IndexedProperty:
                    if (isPut)
                    {
                        if (args.Length != 1) return DISP_E_BADPARAMCOUNT;
                        member.Set!(args, value);
                    }
                    else if (args.Length == 1)
                    {
                        returned = member.Get!(args);
                    }
                    else if (args.Length == 2 && (flags & DISPATCH_METHOD) != 0)
                    {
                        // Python style: obj.SRegisters(index, value)
                        member.Set!([args[0]], args[1]);
                    }
                    else
                    {
                        return DISP_E_BADPARAMCOUNT;
                    }
                    break;
            }

            if (result != IntPtr.Zero) Marshal.GetNativeVariantForObject(returned, result);
            return S_OK;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            if (argError is not null) *argError = 0;
            return DISP_E_TYPEMISMATCH;
        }
        catch (Exception ex)
        {
            var message = ex is AutomationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            if (excepInfo is not null)
            {
                *excepInfo = default;
                excepInfo->Source = Marshal.StringToBSTR("Modbus Master");
                excepInfo->Description = Marshal.StringToBSTR(message);
                excepInfo->Scode = E_FAIL;
            }
            return DISP_E_EXCEPTION;
        }
    }

    // ---- IClassFactory ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateInstance(ComObject* self, IntPtr outer, Guid* riid, void** ppv)
    {
        if (ppv is null) return E_POINTER;
        *ppv = null;
        if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
        try
        {
            var create = (Func<DispatchTarget>)Target(self)!;
            var obj = (ComObject*)CreateDispatch(create());
            int hr;
            if (*riid == IID_IUnknown || *riid == IID_IDispatch)
            {
                Interlocked.Increment(ref obj->RefCount);
                *ppv = obj;
                hr = S_OK;
            }
            else
            {
                hr = E_NOINTERFACE;
            }
            Release(obj);
            return hr;
        }
        catch (Exception)
        {
            return E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LockServer(ComObject* self, int doLock)
    {
        if (doLock != 0) Interlocked.Increment(ref _locks);
        else if (Interlocked.Decrement(ref _locks) == 0 && Volatile.Read(ref _liveObjects) == 0) AllReleased?.Invoke();
        return S_OK;
    }
}
