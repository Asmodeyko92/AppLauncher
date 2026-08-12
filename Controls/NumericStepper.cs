using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AppLauncher.Controls;

public sealed class NumericStepper : Control
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumericStepper), new PropertyMetadata(0d));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumericStepper), new PropertyMetadata(100d));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumericStepper), new PropertyMetadata(1d));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericStepper),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged, CoerceValue));
    public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ValueChanged), RoutingStrategy.Bubble,
        typeof(RoutedPropertyChangedEventHandler<double>), typeof(NumericStepper));

    static NumericStepper() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(NumericStepper), new FrameworkPropertyMetadata(typeof(NumericStepper)));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public event RoutedPropertyChangedEventHandler<double> ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Decrease") is RepeatButton decrease)
            decrease.Click += (_, _) => ChangeValue(-Step);
        if (GetTemplateChild("PART_Increase") is RepeatButton increase)
            increase.Click += (_, _) => ChangeValue(Step);
        if (GetTemplateChild("PART_Value") is TextBox valueBox)
        {
            valueBox.LostKeyboardFocus += (_, _) => CommitText(valueBox);
            valueBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                CommitText(valueBox);
                e.Handled = true;
            };
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsEnabled || e.Delta == 0)
            return;

        ChangeValue(e.Delta > 0 ? Step : -Step);
        e.Handled = true;
    }

    private void ChangeValue(double delta) => Value += delta;

    private void CommitText(TextBox valueBox)
    {
        if (double.TryParse(valueBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || double.TryParse(valueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            Value = value;
        valueBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private static object CoerceValue(DependencyObject target, object value)
    {
        var stepper = (NumericStepper)target;
        return Math.Clamp((double)value, stepper.Minimum, Math.Max(stepper.Minimum, stepper.Maximum));
    }

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        => ((NumericStepper)target).RaiseEvent(new RoutedPropertyChangedEventArgs<double>(
            (double)e.OldValue, (double)e.NewValue, ValueChangedEvent));
}
