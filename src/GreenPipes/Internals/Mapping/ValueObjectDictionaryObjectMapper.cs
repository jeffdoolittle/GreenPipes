﻿// Copyright 2012-2018 Chris Patterson
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace GreenPipes.Internals.Mapping
{
    using System.Collections.Generic;
    using Reflection;


    public class ValueObjectDictionaryObjectMapper<T, TKey, TValue> :
        IObjectMapper<T>
    {
        readonly ReadWriteProperty<T> _property;
        readonly IObjectConverter _valueConverter;

        public ValueObjectDictionaryObjectMapper(ReadWriteProperty<T> property, IObjectConverter valueConverter)
        {
            _property = property;
            _valueConverter = valueConverter;
        }

        public void ApplyTo(T obj, IObjectValueProvider valueProvider)
        {
            IArrayValueProvider values;
            if (!valueProvider.TryGetValue(_property.Property.Name, out values))
                return;

            var elements = new Dictionary<TKey, TValue>();

            for (var i = 0;; i++)
            {
                IArrayValueProvider elementArray;
                if (!values.TryGetValue(i, out elementArray))
                    break;

                TKey elementKey;
                if (elementArray.TryGetValue(0, out elementKey))
                {
                    var elementValue = default(TValue);
                    IObjectValueProvider elementValueProvider;
                    if (elementArray.TryGetValue(1, out elementValueProvider))
                        elementValue = (TValue)_valueConverter.GetObject(elementValueProvider);

                    elements[elementKey] = elementValue;
                }
            }

            _property.Set(obj, elements);
        }
    }
}