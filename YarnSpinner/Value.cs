using System;
using System.Globalization;

namespace Yarn {
    // A value from inside Yarn.
    public class Value : IComparable, IComparable<Value> {
        public static readonly Value NULL = new Value();

        public enum Type {
            Number, // a constant number
            String, // a string
            Bool, // a boolean value
            Variable, // the name of a variable; will be expanded at runtime
            Null, // the null value
        }

        public Type type { get; internal set; }

        // The underlying values for this object
        internal float numberValue { get; private set; }
        internal string variableName { get; set; }
        internal string stringValue { get; private set; }
        internal bool boolValue { get; private set; }

        object backingValue {
            get {
                switch (type) {
                    case Type.Null:
                        return null;
                    case Type.String:
                        return stringValue;
                    case Type.Number:
                        return numberValue;
                    case Type.Bool:
                        return boolValue;
                }
                throw new InvalidOperationException(
                    string.Format("Can't get good backing type for {0}", type)
                    );
            }
        }

        public float AsNumber {
            get {
                switch (type) {
                    case Type.Number:
                        return numberValue;
                    case Type.String:
                        float value;
                        try {
                            if (float.TryParse(stringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                                return value;
                            return 0.0f;
                        }
                        catch (FormatException) {
                            return 0.0f;
                        }
                    case Type.Bool:
                        return boolValue ? 1.0f : 0.0f;
                    case Type.Null:
                        return 0.0f;
                    default:
                        throw new InvalidOperationException("Cannot cast to number from " + type.ToString());
                }
            }
        }

        public bool AsBool {
            get {
                switch (type) {
                    case Type.Number:
                        return !float.IsNaN(numberValue) && numberValue != 0.0f;
                    case Type.String:
                        return !String.IsNullOrEmpty(stringValue);
                    case Type.Bool:
                        return boolValue;
                    case Type.Null:
                        return false;
                    default:
                        throw new InvalidOperationException("Cannot cast to bool from " + type.ToString());
                }
            }
        }

        public string AsString {
            get {
                switch (type) {
                    case Type.Number:
                        if (float.IsNaN(numberValue)) {
                            return "NaN";
                        }
                        return numberValue.ToString();
                    case Type.String:
                        return stringValue;
                    case Type.Bool:
                        return boolValue.ToString();
                    case Type.Null:
                        return "null";
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        // Create a null value
        public Value() : this(null) {}

        // Create a value with a C# object
        public Value(object value) {
            // Copy an existing value
            var valueAsValue = value as Value;
            if (valueAsValue != null) {
                type = valueAsValue.type;
                switch (type) {
                    case Type.Number:
                        numberValue = valueAsValue.numberValue;
                        break;
                    case Type.String:
                        stringValue = valueAsValue.stringValue;
                        break;
                    case Type.Bool:
                        boolValue = valueAsValue.boolValue;
                        break;
                    case Type.Variable:
                        variableName = valueAsValue.variableName;
                        break;
                    case Type.Null:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                return;
            }
            if (value == null) {
                type = Type.Null;
                return;
            }
            var valueAsString = value as String;
            if (valueAsString != null) {
                type = Type.String;
                stringValue = valueAsString;
                return;
            }
            if (value is int ||
                value is float ||
                value is double) {
                type = Type.Number;
                numberValue = Convert.ToSingle(value);
                return;
            }
            if (value is bool) {
                type = Type.Bool;
                boolValue = Convert.ToBoolean(value);
                return;
            }
            var error = string.Format("Attempted to create a Value using a {0}; currently, " +
                                      "Values can only be numbers, strings, bools or null.", value.GetType().Name);
            throw new YarnException(error);
        }

        public virtual int CompareTo(object obj) {
            if (obj == null)
                return 1;

            // soft, fast coercion
            var other = obj as Value;

            // not a value
            if (other == null)
                throw new ArgumentException("Object is not a Value");

            // it is a value!
            return CompareTo(other);
        }

        public virtual int CompareTo(Value other) {
            if (other == null) {
                return 1;
            }

            if (other.type == type) {
                switch (type) {
                    case Type.Null:
                        return 0;
                    case Type.String:
                        return stringValue.CompareTo(other.stringValue);
                    case Type.Number:
                        return numberValue.CompareTo(other.numberValue);
                    case Type.Bool:
                        return boolValue.CompareTo(other.boolValue);
                }
            }

            // try to do a string test at that point!
            return AsString.CompareTo(other.AsString);
        }

        public override bool Equals(object obj) {
            if (obj == null || GetType() != obj.GetType()) {
                return false;
            }

            var other = (Value)obj;

            switch (type) {
                case Type.Number:
                    return AsNumber == other.AsNumber;
                case Type.String:
                    return AsString == other.AsString;
                case Type.Bool:
                    return AsBool == other.AsBool;
                case Type.Null:
                    return other.type == Type.Null || other.AsNumber == 0 || other.AsBool == false;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        // override object.GetHashCode
        public override int GetHashCode() {
            var backing = backingValue;

            // TODO: yeah hay maybe fix this
            if (backing != null) {
                return backing.GetHashCode();
            }

            return 0;
        }

        public override string ToString() {
            return string.Format("[Value: type={0}, AsNumber={1}, AsBool={2}, AsString={3}]", type, AsNumber, AsBool, AsString);
        }

        public static Value operator +(Value a, Value b) {
            // catches:
            // undefined + string
            // number + string
            // string + string
            // bool + string
            // null + string
            if (a.type == Type.String || b.type == Type.String) {
                // we're headed for string town!
                return new Value(a.AsString + b.AsString);
            }

            // catches:
            // number + number
            // bool (=> 0 or 1) + number
            // null (=> 0) + number
            // bool (=> 0 or 1) + bool (=> 0 or 1)
            // null (=> 0) + null (=> 0)
            if ((a.type == Type.Number || b.type == Type.Number) ||
                (a.type == Type.Bool && b.type == Type.Bool) ||
                (a.type == Type.Null && b.type == Type.Null)
                ) {
                return new Value(a.AsNumber + b.AsNumber);
            }

            throw new ArgumentException(
                string.Format("Cannot add types {0} and {1}.", a.type, b.type)
                );
        }

        public static Value operator -(Value a, Value b) {
            if (a.type == Type.Number && (b.type == Type.Number || b.type == Type.Null) ||
                b.type == Type.Number && (a.type == Type.Number || a.type == Type.Null)
                ) {
                return new Value(a.AsNumber - b.AsNumber);
            }

            throw new ArgumentException(
                string.Format("Cannot subtract types {0} and {1}.", a.type, b.type)
                );
        }

        public static Value operator *(Value a, Value b) {
            if (a.type == Type.Number && (b.type == Type.Number || b.type == Type.Null) ||
                b.type == Type.Number && (a.type == Type.Number || a.type == Type.Null)
                ) {
                return new Value(a.AsNumber * b.AsNumber);
            }

            throw new ArgumentException(
                string.Format("Cannot multiply types {0} and {1}.", a.type, b.type)
                );
        }

        public static Value operator /(Value a, Value b) {
            if (a.type == Type.Number && (b.type == Type.Number || b.type == Type.Null) ||
                b.type == Type.Number && (a.type == Type.Number || a.type == Type.Null)
                ) {
                return new Value(a.AsNumber / b.AsNumber);
            }

            throw new ArgumentException(
                string.Format("Cannot divide types {0} and {1}.", a.type, b.type)
                );
        }

        public static Value operator -(Value a) {
            if (a.type == Type.Number) {
                return new Value(-a.AsNumber);
            }
            if (a.type == Type.Null &&
                a.type == Type.String &&
                (a.AsString == null || a.AsString.Trim() == "")
                ) {
                return new Value(-0);
            }
            return new Value(float.NaN);
        }

        // Define the is greater than operator.
        public static bool operator >(Value operand1, Value operand2) {
            return operand1.CompareTo(operand2) == 1;
        }

        // Define the is less than operator.
        public static bool operator <(Value operand1, Value operand2) {
            return operand1.CompareTo(operand2) == -1;
        }

        // Define the is greater than or equal to operator.
        public static bool operator >=(Value operand1, Value operand2) {
            return operand1.CompareTo(operand2) >= 0;
        }

        // Define the is less than or equal to operator.
        public static bool operator <=(Value operand1, Value operand2) {
            return operand1.CompareTo(operand2) <= 0;
        }
    }
}
