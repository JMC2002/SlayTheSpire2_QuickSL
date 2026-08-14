using System.Reflection;
using System.Reflection.Emit;

namespace QuickSL.Core;

/// <summary>
/// 为运行时接口生成轻量转发代理，避免 <see cref="DispatchProxy"/> 使用加载上下文名称
/// 拼接动态程序集名。游戏安装路径含单引号时，后者会生成无法解析的程序集名。
/// </summary>
internal static class RuntimeInterfaceProxy
{
    private const string AssemblyName = "QuickSL.RuntimeInterfaceProxies";

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<Type, ProxyTypeInfo> ProxyTypes = [];
    private static readonly AssemblyBuilder ProxyAssembly = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName(AssemblyName),
        AssemblyBuilderAccess.Run);
    private static readonly ModuleBuilder ProxyModule = ProxyAssembly.DefineDynamicModule(AssemblyName);
    private static readonly MethodInfo HandlerInvokeMethod =
        typeof(Func<MethodInfo, object?[]?, object?>).GetMethod(nameof(Func<MethodInfo, object?[]?, object?>.Invoke))!;
    private static readonly MethodInfo GetTypeFromHandleMethod =
        typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!;
    private static readonly MethodInfo MakeGenericMethodMethod =
        typeof(MethodInfo).GetMethod(nameof(MethodInfo.MakeGenericMethod), [typeof(Type[])])!;

    private static int nextTypeId;

    internal static object Create(
        Type interfaceType,
        Func<MethodInfo, object?[]?, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentNullException.ThrowIfNull(handler);

        if (!interfaceType.IsInterface)
        {
            throw new ArgumentException($"{interfaceType.FullName} 不是接口。", nameof(interfaceType));
        }

        ProxyTypeInfo proxyTypeInfo;
        lock (SyncRoot)
        {
            if (!ProxyTypes.TryGetValue(interfaceType, out proxyTypeInfo))
            {
                proxyTypeInfo = CreateProxyType(interfaceType);
                ProxyTypes.Add(interfaceType, proxyTypeInfo);
            }
        }

        return Activator.CreateInstance(proxyTypeInfo.ProxyType, handler, proxyTypeInfo.Methods)
            ?? throw new InvalidOperationException($"无法创建 {interfaceType.FullName} 的运行时代理。");
    }

    private static ProxyTypeInfo CreateProxyType(Type interfaceType)
    {
        MethodInfo[] methods = GetInterfaceMethods(interfaceType);
        int typeId = Interlocked.Increment(ref nextTypeId);
        TypeBuilder typeBuilder = ProxyModule.DefineType(
            $"QuickSL.RuntimeInterfaceProxy_{typeId}",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        typeBuilder.AddInterfaceImplementation(interfaceType);

        FieldBuilder handlerField = typeBuilder.DefineField(
            "_handler",
            typeof(Func<MethodInfo, object?[]?, object?>),
            FieldAttributes.Private | FieldAttributes.InitOnly);
        FieldBuilder methodsField = typeBuilder.DefineField(
            "_methods",
            typeof(MethodInfo[]),
            FieldAttributes.Private | FieldAttributes.InitOnly);

        DefineConstructor(typeBuilder, handlerField, methodsField);
        for (int i = 0; i < methods.Length; i++)
        {
            DefineInterfaceMethod(typeBuilder, handlerField, methodsField, methods[i], i);
        }

        return new ProxyTypeInfo(
            typeBuilder.CreateType()
                ?? throw new InvalidOperationException($"无法生成 {interfaceType.FullName} 的运行时代理类型。"),
            methods);
    }

    private static MethodInfo[] GetInterfaceMethods(Type interfaceType)
    {
        return interfaceType
            .GetInterfaces()
            .Append(interfaceType)
            .SelectMany(static type => type.GetMethods())
            .Distinct()
            .ToArray();
    }

    private static void DefineConstructor(
        TypeBuilder typeBuilder,
        FieldBuilder handlerField,
        FieldBuilder methodsField)
    {
        ConstructorBuilder constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.HasThis,
            [typeof(Func<MethodInfo, object?[]?, object?>), typeof(MethodInfo[])]);
        ILGenerator il = constructor.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, handlerField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stfld, methodsField);
        il.Emit(OpCodes.Ret);
    }

    private static void DefineInterfaceMethod(
        TypeBuilder typeBuilder,
        FieldBuilder handlerField,
        FieldBuilder methodsField,
        MethodInfo interfaceMethod,
        int methodIndex)
    {
        MethodAttributes attributes = MethodAttributes.Public
            | MethodAttributes.Virtual
            | MethodAttributes.Final
            | MethodAttributes.HideBySig
            | MethodAttributes.NewSlot;
        if (interfaceMethod.IsSpecialName)
        {
            attributes |= MethodAttributes.SpecialName;
        }

        MethodBuilder methodBuilder = typeBuilder.DefineMethod(
            interfaceMethod.Name,
            attributes,
            interfaceMethod.CallingConvention);
        IReadOnlyDictionary<Type, Type> genericTypeMap =
            DefineGenericParameters(methodBuilder, interfaceMethod);

        Type returnType = ReplaceGenericParameters(interfaceMethod.ReturnType, genericTypeMap);
        ParameterInfo[] sourceParameters = interfaceMethod.GetParameters();
        Type[] parameterTypes = sourceParameters
            .Select(parameter => ReplaceGenericParameters(parameter.ParameterType, genericTypeMap))
            .ToArray();
        methodBuilder.SetReturnType(returnType);
        methodBuilder.SetParameters(parameterTypes);

        for (int i = 0; i < sourceParameters.Length; i++)
        {
            methodBuilder.DefineParameter(i + 1, sourceParameters[i].Attributes, sourceParameters[i].Name);
        }

        EmitMethodBody(
            methodBuilder,
            handlerField,
            methodsField,
            sourceParameters,
            parameterTypes,
            returnType,
            methodIndex);
        typeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
    }

    private static IReadOnlyDictionary<Type, Type> DefineGenericParameters(
        MethodBuilder methodBuilder,
        MethodInfo interfaceMethod)
    {
        Type[] sourceGenericParameters = interfaceMethod.GetGenericArguments();
        if (sourceGenericParameters.Length == 0)
        {
            return new Dictionary<Type, Type>();
        }

        GenericTypeParameterBuilder[] targetGenericParameters = methodBuilder.DefineGenericParameters(
            sourceGenericParameters.Select(static parameter => parameter.Name).ToArray());
        var genericTypeMap = new Dictionary<Type, Type>(sourceGenericParameters.Length);
        for (int i = 0; i < sourceGenericParameters.Length; i++)
        {
            genericTypeMap.Add(sourceGenericParameters[i], targetGenericParameters[i]);
        }

        for (int i = 0; i < sourceGenericParameters.Length; i++)
        {
            Type sourceParameter = sourceGenericParameters[i];
            GenericTypeParameterBuilder targetParameter = targetGenericParameters[i];
            targetParameter.SetGenericParameterAttributes(sourceParameter.GenericParameterAttributes);

            Type[] constraints = sourceParameter
                .GetGenericParameterConstraints()
                .Select(constraint => ReplaceGenericParameters(constraint, genericTypeMap))
                .ToArray();
            Type? baseTypeConstraint = constraints.FirstOrDefault(static constraint => !constraint.IsInterface);
            if (baseTypeConstraint != null)
            {
                targetParameter.SetBaseTypeConstraint(baseTypeConstraint);
            }

            Type[] interfaceConstraints = constraints.Where(static constraint => constraint.IsInterface).ToArray();
            if (interfaceConstraints.Length > 0)
            {
                targetParameter.SetInterfaceConstraints(interfaceConstraints);
            }
        }

        return genericTypeMap;
    }

    private static Type ReplaceGenericParameters(
        Type type,
        IReadOnlyDictionary<Type, Type> genericTypeMap)
    {
        if (genericTypeMap.TryGetValue(type, out Type? replacement))
        {
            return replacement;
        }

        if (type.IsByRef)
        {
            return ReplaceGenericParameters(type.GetElementType()!, genericTypeMap).MakeByRefType();
        }

        if (type.IsPointer)
        {
            return ReplaceGenericParameters(type.GetElementType()!, genericTypeMap).MakePointerType();
        }

        if (type.IsArray)
        {
            Type elementType = ReplaceGenericParameters(type.GetElementType()!, genericTypeMap);
            return type.GetArrayRank() == 1
                ? elementType.MakeArrayType()
                : elementType.MakeArrayType(type.GetArrayRank());
        }

        if (type.IsGenericType)
        {
            Type[] genericArguments = type
                .GetGenericArguments()
                .Select(argument => ReplaceGenericParameters(argument, genericTypeMap))
                .ToArray();
            return type.GetGenericTypeDefinition().MakeGenericType(genericArguments);
        }

        return type;
    }

    private static void EmitMethodBody(
        MethodBuilder methodBuilder,
        FieldBuilder handlerField,
        FieldBuilder methodsField,
        IReadOnlyList<ParameterInfo> sourceParameters,
        IReadOnlyList<Type> parameterTypes,
        Type returnType,
        int methodIndex)
    {
        ILGenerator il = methodBuilder.GetILGenerator();
        LocalBuilder argsLocal = il.DeclareLocal(typeof(object[]));
        LocalBuilder resultLocal = il.DeclareLocal(typeof(object));

        EmitArgumentsArray(il, argsLocal, sourceParameters, parameterTypes);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, handlerField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, methodsField);
        EmitInt32(il, methodIndex);
        il.Emit(OpCodes.Ldelem_Ref);
        EmitConstructedMethodInfo(il, methodBuilder);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, HandlerInvokeMethod);
        il.Emit(OpCodes.Stloc, resultLocal);

        EmitByRefResults(il, argsLocal, parameterTypes);

        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Ret);
            return;
        }

        il.Emit(OpCodes.Ldloc, resultLocal);
        EmitObjectConversion(il, returnType);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitArgumentsArray(
        ILGenerator il,
        LocalBuilder argsLocal,
        IReadOnlyList<ParameterInfo> sourceParameters,
        IReadOnlyList<Type> parameterTypes)
    {
        EmitInt32(il, parameterTypes.Count);
        il.Emit(OpCodes.Newarr, typeof(object));
        il.Emit(OpCodes.Stloc, argsLocal);

        for (int i = 0; i < parameterTypes.Count; i++)
        {
            Type parameterType = parameterTypes[i];
            il.Emit(OpCodes.Ldloc, argsLocal);
            EmitInt32(il, i);

            if (parameterType.IsByRef)
            {
                if (sourceParameters[i].IsOut && !sourceParameters[i].IsIn)
                {
                    il.Emit(OpCodes.Ldnull);
                }
                else
                {
                    Type elementType = parameterType.GetElementType()!;
                    il.Emit(OpCodes.Ldarg, i + 1);
                    il.Emit(OpCodes.Ldobj, elementType);
                    EmitBoxIfNeeded(il, elementType);
                }
            }
            else
            {
                il.Emit(OpCodes.Ldarg, i + 1);
                EmitBoxIfNeeded(il, parameterType);
            }

            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    private static void EmitConstructedMethodInfo(ILGenerator il, MethodBuilder methodBuilder)
    {
        GenericTypeParameterBuilder[] genericParameters = methodBuilder.GetGenericArguments()
            .OfType<GenericTypeParameterBuilder>()
            .ToArray();
        if (genericParameters.Length == 0)
        {
            return;
        }

        EmitInt32(il, genericParameters.Length);
        il.Emit(OpCodes.Newarr, typeof(Type));
        for (int i = 0; i < genericParameters.Length; i++)
        {
            il.Emit(OpCodes.Dup);
            EmitInt32(il, i);
            il.Emit(OpCodes.Ldtoken, genericParameters[i]);
            il.Emit(OpCodes.Call, GetTypeFromHandleMethod);
            il.Emit(OpCodes.Stelem_Ref);
        }

        il.Emit(OpCodes.Callvirt, MakeGenericMethodMethod);
    }

    private static void EmitByRefResults(
        ILGenerator il,
        LocalBuilder argsLocal,
        IReadOnlyList<Type> parameterTypes)
    {
        for (int i = 0; i < parameterTypes.Count; i++)
        {
            Type parameterType = parameterTypes[i];
            if (!parameterType.IsByRef)
            {
                continue;
            }

            Type elementType = parameterType.GetElementType()!;
            il.Emit(OpCodes.Ldarg, i + 1);
            il.Emit(OpCodes.Ldloc, argsLocal);
            EmitInt32(il, i);
            il.Emit(OpCodes.Ldelem_Ref);
            EmitObjectConversion(il, elementType);
            il.Emit(OpCodes.Stobj, elementType);
        }
    }

    private static void EmitObjectConversion(ILGenerator il, Type targetType)
    {
        if (targetType.IsValueType || targetType.IsGenericParameter)
        {
            il.Emit(OpCodes.Unbox_Any, targetType);
        }
        else
        {
            il.Emit(OpCodes.Castclass, targetType);
        }
    }

    private static void EmitBoxIfNeeded(ILGenerator il, Type type)
    {
        if (type.IsValueType || type.IsGenericParameter)
        {
            il.Emit(OpCodes.Box, type);
        }
    }

    private static void EmitInt32(ILGenerator il, int value)
    {
        switch (value)
        {
            case -1:
                il.Emit(OpCodes.Ldc_I4_M1);
                return;
            case 0:
                il.Emit(OpCodes.Ldc_I4_0);
                return;
            case 1:
                il.Emit(OpCodes.Ldc_I4_1);
                return;
            case 2:
                il.Emit(OpCodes.Ldc_I4_2);
                return;
            case 3:
                il.Emit(OpCodes.Ldc_I4_3);
                return;
            case 4:
                il.Emit(OpCodes.Ldc_I4_4);
                return;
            case 5:
                il.Emit(OpCodes.Ldc_I4_5);
                return;
            case 6:
                il.Emit(OpCodes.Ldc_I4_6);
                return;
            case 7:
                il.Emit(OpCodes.Ldc_I4_7);
                return;
            case 8:
                il.Emit(OpCodes.Ldc_I4_8);
                return;
        }

        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4, value);
        }
    }

    private readonly record struct ProxyTypeInfo(Type ProxyType, MethodInfo[] Methods);
}
