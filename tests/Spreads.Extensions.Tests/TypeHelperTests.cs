﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using System.Threading;
using Spreads.Serialization;
using Spreads.Storage;


namespace Spreads.Core.Tests {



    [TestFixture]
    public class TypeHelperTests {


        public class MyPoco {
            public string String { get; set; }
            public long Long { get; set; }
        }

        [Test]
        public void CouldGetSizeOfDoubleArray() {
            Console.WriteLine(TypeHelper<double[]>.Size);

        }

        [Test]
        public void CouldGetSizeOfReferenceType() {

            Console.WriteLine(TypeHelper<string>.Size);

        }


        [Test]
        public void CouldWritePOCO() {

            var ptr = Marshal.AllocHGlobal(1024);
            var myPoco = new MyPoco {
                String = "MyString",
                Long = 123
            };
            TypeHelper<MyPoco>.StructureToPtr(myPoco, ptr);
            var newPoco = TypeHelper<MyPoco>.PtrToStructure(ptr);
            Assert.AreEqual(myPoco.String, newPoco.String);
            Assert.AreEqual(myPoco.Long, newPoco.Long);

        }


        [Test]
        public void CouldWritePOCOToBuffer() {

            var ptr = Marshal.AllocHGlobal(1024);
            var buffer = new DirectBuffer(1024, ptr);
            var myPoco = new MyPoco {
                String = "MyString",
                Long = 123
            };
            buffer.Write(0, myPoco);
            var newPoco = buffer.Read<MyPoco>(0);
            Assert.AreEqual(myPoco.String, newPoco.String);
            Assert.AreEqual(myPoco.Long, newPoco.Long);

        }

        [Test]
        public void CouldWriteArray() {

            var ptr = Marshal.AllocHGlobal(1024);
            var myArray = new int[2];
            myArray[0] = 123;
            myArray[1] = 456;

            TypeHelper<int[]>.StructureToPtr(myArray, ptr);

            var newArray = TypeHelper<int[]>.PtrToStructure(ptr);
            Assert.IsTrue(myArray.SequenceEqual(newArray));

        }

        [Test]
        public void CouldWriteArrayToBuffer() {

            var ptr = Marshal.AllocHGlobal(1024);
            var buffer = new DirectBuffer(1024, ptr);
            var myArray = new int[2];
            myArray[0] = 123;
            myArray[1] = 456;

            buffer.Write(0, myArray);
            var newArray = buffer.Read<int[]>(0);
            Assert.IsTrue(myArray.SequenceEqual(newArray));

        }



        [Test]
        public void CouldWriteComplexTypeWithConverterToBuffer() {

            var ptr = Marshal.AllocHGlobal(1024);
            var buffer = new DirectBuffer(1024, ptr);


            var myStruct = new SetRemoveCommandBody<long, string>()
            {
                key = 123,
                value = "string value"
            };

            buffer.Write(0, myStruct);
            var newStruct = buffer.Read<SetRemoveCommandBody<long, string>>(0);
            Assert.AreEqual(myStruct.key, newStruct.key);
            Assert.AreEqual(myStruct.value, newStruct.value);

        }


    }
}
