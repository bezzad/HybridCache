﻿using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HybridRedisCache.Test
{
    public class ObjectHelperTest
    {
        private ComplexObject _value1;
        private ComplexPocoObject _value2;

        public ObjectHelperTest()
        {
            _value1 = new ComplexObject
            {
                Name = "John",
                Age = 30,
                Address = new Address
                {
                    Street = "123 Main St",
                    City = "Anytown",
                    State = "CA",
                    Zip = "12345"
                },
                PhoneNumbers = new List<string> { "555-1234", "555-5678" }
            };

            _value2 = new ComplexPocoObject
            {
                Name = "John",
                Age = 30,
                Address = new Location
                {
                    Lat = 3.3,
                    Lan = 4.4,
                    Street = "123 Main St",
                    City = "Anytown",
                    State = "CA",
                    Zip = "12345"
                },
                PhoneNumbers = new List<string> { "555-1234", "555-5678" },
                Parent = _value1
            };
        }

        [Fact]
        public void DeserializePolymorphicClasses()
        {
            // Act
            var json = _value2.Serialize();
            var result = json.Deserialize<IComplexObject>();
            var realTypeResult = result as ComplexPocoObject;

            // Assert
            // verify that the retrieved object is equal to the original object
            Assert.NotNull(json);
            Assert.NotNull(result);
            Assert.NotNull(realTypeResult);
            Assert.IsType<ComplexPocoObject>(result);
            Assert.IsType<ComplexObject>(realTypeResult.Parent);
            Assert.IsType<Address>(realTypeResult.Parent.Address);
            Assert.IsType<Location>(realTypeResult.Address);
            Assert.Equal(_value2.Name, result.Name);
            Assert.Equal(_value2.PhoneNumbers.First(), result.PhoneNumbers.First());
            Assert.Equal(_value2.Parent.Address.City, realTypeResult.Parent.Address.City);
        }

        [Fact]
        public void DeserializeSimpleJson()
        {
            // Arrange
            string json = @"{
                ""name"": ""John"",
                ""age"": 30,
                ""address"": {
                    ""$type"": ""HybridRedisCache.Test.Address, HybridRedisCache.Test"",
                    ""street"": ""123 Main St"",
                    ""city"": ""Anytown"",
                    ""state"": ""CA"",
                    ""zip"": ""12345""
                },
                ""phoneNumbers"": [
                    ""555-1234"",
                    ""555-5678""
                ]
            }";

            // Act
            var result = json.Deserialize<ComplexObject>();

            // Assert
            // verify that the retrieved object is equal to the original object
            Assert.NotNull(result);
            Assert.IsType<ComplexObject>(result);
            Assert.IsType<Address>(result.Address);
            Assert.Equal(_value1.Name, result.Name);
            Assert.Equal(_value1.PhoneNumbers.First(), result.PhoneNumbers.First());
            Assert.Equal(_value1.Address.City, result.Address.City);
        }

        [Fact]
        public void PrimitivesTypeSerializationTest()
        {
            // Arrange
            double doubleNum = 123456789.0123456789;
            float floatNum = 123456.012345f;
            int intNum = 1234;
            short shortNum = 1234;
            char character = 'A';
            string text = "This is a sample text";

            // Act and Assert
            PrimitivesTypeTest(doubleNum);
            PrimitivesTypeTest(floatNum);
            PrimitivesTypeTest(intNum);
            PrimitivesTypeTest(shortNum);
            PrimitivesTypeTest(character);
            PrimitivesTypeTest(text);
        }

        private void PrimitivesTypeTest<T>(T value)
        {
            // Arrange
            var json = value.Serialize();

            // Act
            var result = json.Deserialize<T>();

            // Assert
            // verify that the retrieved object is equal to the original object
            Assert.NotNull(result);
            Assert.IsType<T>(result);
            Assert.Equal(value, result);
        }

        [Fact]
        public void ToTimeSpanTest()
        {
            // Arrange
            DateTime? date = DateTime.UtcNow.AddDays(16).AddHours(8).AddMinutes(40).AddSeconds(20);

            // Act
            var time = date.ToTimeSpan();

            // Assert
            // verify that the retrieved object is equal to the original object
            Assert.Equal(16, time.Days);
            Assert.Equal(8, time.Hours);
            Assert.Equal(40, time.Minutes);
        }
    }
}