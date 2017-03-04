﻿using Avalonia.Data;
using Avalonia.Markup.Xaml.Context;
using Portable.Xaml;
using Portable.Xaml.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using am = Avalonia.Metadata;

namespace Avalonia.Markup.Xaml.PortableXaml
{
    internal class AvaloniaXamlSchemaContext : XamlSchemaContext
    {
        public AvaloniaXamlSchemaContext(IRuntimeTypeProvider typeProvider)
        //better not set the references assemblies
        //: base(typeProvider.ReferencedAssemblies)
        {
            _avaloniaTypeProvider = typeProvider;
        }

        private IRuntimeTypeProvider _avaloniaTypeProvider;

        protected override XamlType GetXamlType(string xamlNamespace, string name, params XamlType[] typeArguments)
        {
            XamlType type = null;
            try
            {
                type = ResolveXamlTypeName(xamlNamespace, name, typeArguments, false);

                if (type == null)
                {
                    type = base.GetXamlType(xamlNamespace, name, typeArguments);
                }
            }
            catch (Exception e)
            {
                //TODO: log or wrap exception
                throw e;
            }
            return type;
        }

        private XamlType ResolveXamlTypeName(string xmlNamespace, string xmlLocalName, XamlType[] typeArguments, bool required)
        {
            Type[] genArgs = null;
            if (typeArguments != null && typeArguments.Any())
            {
                genArgs = typeArguments.Select(t => t?.UnderlyingType).ToArray();

                if (genArgs.Any(t => t == null))
                {
                    return null;
                }
            }

            // MarkupExtension type could omit "Extension" part in XML name.
            Type type = _avaloniaTypeProvider.FindType(xmlNamespace,
                                                        xmlLocalName,
                                                        genArgs) ??
                        _avaloniaTypeProvider.FindType(xmlNamespace,
                                                        xmlLocalName + "Extension",
                                                        genArgs);

            if (type == null)
            {
                //let's try the simple types
                //in Portable xaml like xmlns:sys='clr-namespace:System;assembly=mscorlib'
                //and sys:Double is not resolved properly
                return ResolveSimpleTypeName(xmlNamespace, xmlLocalName);
            }

            return GetXamlType(type);
        }

        #region Workaround for bug in Portablexaml system types like double,int etc ...

        private static Type[] _simpleTypes = new Type[]
        {
            typeof(bool),
            typeof(byte),
            typeof(char),
            typeof(decimal),
            typeof(double),
            typeof(Int16),
            typeof(Int32),
            typeof(Int64),
            typeof(float),
            typeof(string),
            typeof(TimeSpan),
            typeof(Uri),
        };

        private static Dictionary<Tuple<string, string>, XamlType> _simpleXamlTypes;

        //in Portable xaml like xmlns:sys='clr-namespace:System;assembly=mscorlib'
        //and sys:Double is not resolved properly
        [Obsolete("TODO: remove once it's fixed in Portable.xaml")]
        private static XamlType ResolveSimpleTypeName(string xmlNamespace, string xmlLocalName)
        {
            if (_simpleXamlTypes == null)
            {
                _simpleXamlTypes = new Dictionary<Tuple<string, string>, XamlType>();

                foreach (var type in _simpleTypes)
                {
                    string asmName = type.GetTypeInfo().Assembly.GetName().Name;
                    string ns = $"clr-namespace:{type.Namespace};assembly={asmName}";
                    var xamlType = XamlLanguage.AllTypes.First(t => t.UnderlyingType == type);
                    _simpleXamlTypes.Add(new Tuple<string, string>(ns, type.Name), xamlType);
                }
            }

            XamlType result;

            var key = new Tuple<string, string>(xmlNamespace, xmlLocalName);

            _simpleXamlTypes.TryGetValue(key, out result);

            return result;
        }

        #endregion Workaround for bug in Portablexaml system types like double,int etc ...

        protected override ICustomAttributeProvider GetCustomAttributeProvider(Type type)
                                    => new AvaloniaTypeAttributeProvider(type);

        protected override ICustomAttributeProvider GetCustomAttributeProvider(MemberInfo member)
                                    => new AvaloniaMemberAttributeProvider(member);

        public override XamlType GetXamlType(Type type)
        {
            XamlType result = null;

            if (_cachedTypes.TryGetValue(type, out result))
            {
                return result;
            }

            _cachedTypes[type] = result = GetAvaloniaXamlType(type) ?? base.GetXamlType(type);

            return result;
        }

        private XamlType GetAvaloniaXamlType(Type type)
        {
            if (typeof(IBinding).GetTypeInfo().IsAssignableFrom(type.GetTypeInfo()))
            {
                return BindingXamlType.Create(type, this);
            }

            //TODO: do we need it ???
            //if (type.FullName.StartsWith("Avalonia."))
            //{
            //    return new AvaloniaXamlType(type, this);
            //}

            return null;
        }

        protected override XamlMember GetAttachableProperty(string attachablePropertyName, MethodInfo getter, MethodInfo setter)
        {
            var key = new Tuple<MemberInfo, MemberInfo>(getter, setter);

            XamlMember result;

            if (_cachedMembers.TryGetValue(key, out result))
            {
                return result;
            }

            var type = (getter ?? setter).DeclaringType;

            var prop = AvaloniaPropertyRegistry.Instance.GetAttached(type)
                    .FirstOrDefault(v => v.Name == attachablePropertyName);

            if (prop != null)
            {
                result = new AvaloniaAttachedPropertyXamlMember(
                                        prop, attachablePropertyName,
                                        getter, setter, this);
            }

            if (result == null)
            {
                result = base.GetAttachableProperty(attachablePropertyName, getter, setter);
            }

            return _cachedMembers[key] = result;
        }

        protected override XamlMember GetProperty(PropertyInfo pi)
        {
            Type objType = pi.DeclaringType;
            string name = pi.Name;

            XamlMember result;

            var key = new Tuple<MemberInfo, MemberInfo>(pi, null);

            if (_cachedMembers.TryGetValue(key, out result))
            {
                return result;
            }

            var avProp = AvaloniaPropertyRegistry.Instance.FindRegistered(objType, name);

            var assignBindingAttr = pi.GetCustomAttribute<AssignBindingAttribute>();

            if (avProp != null)
            {
                result = new AvaloniaPropertyXamlMember(avProp, pi, this)
                {
                    AssignBinding = assignBindingAttr != null
                };
            }

            if (result == null)
            {
                var dependAttr = pi.GetCustomAttribute<am.DependsOnAttribute>();

                if (dependAttr != null)
                {
                    result = new DependOnXamlMember(dependAttr.Name, pi, this);
                }
            }

            if (result == null)
            {
                result = new PropertyXamlMember(pi, this);
            }

            return _cachedMembers[key] = result;
        }

        private Dictionary<Type, XamlType> _cachedTypes = new Dictionary<Type, XamlType>();

        private Dictionary<Tuple<MemberInfo, MemberInfo>, XamlMember> _cachedMembers =
                        new Dictionary<Tuple<MemberInfo, MemberInfo>, XamlMember>();
    }
}