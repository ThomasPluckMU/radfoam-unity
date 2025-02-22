
using System;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

namespace Ply
{
    [CreateAssetMenu(fileName = "PlyData", menuName = "Scriptable Objects/PlyData")]
    [PreferBinarySerialization]
    public class PlyData : ScriptableObject
    {
        [SerializeField]
        private List<Element> elements;

        public void Init(List<Element> elements)
        {
            this.elements = elements;
        }


        public Element Element(string name)
        {
            return elements.Find(element => element.name == name);
        }
    }

    [System.Serializable]
    public enum DataType
    {
        Float, UChar, UInt
    }

    [System.Serializable]
    public class Property
    {
        public string name;
        public DataType type;
        public byte[] data;


        public float as_float(int index)
        {
            return BitConverter.ToSingle(data, index * sizeof(float));
        }

        public byte as_byte(int index)
        {
            return data[index]; // lol
        }

        public uint as_uint(int index)
        {
            return BitConverter.ToUInt32(data, index * sizeof(uint));
        }
    }

    [System.Serializable]
    public class Element
    {
        public string name;
        public int count;
        public List<Property> properties;

        public Property Property(string name)
        {
            return properties.Find(property => property.name == name);
        }

        public void Debug()
        {
            foreach (var prop in properties) {
                UnityEngine.Debug.Log(prop.name);
            }
        }
    }
}

