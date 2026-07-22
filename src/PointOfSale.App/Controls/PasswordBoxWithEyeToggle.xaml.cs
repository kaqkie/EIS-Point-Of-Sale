using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PointOfSale.App.Controls;

/// <summary>
/// Secure password entry with an interactive eye toggle that reveals/masks text
/// without dropping caret context. Password is exposed as a bindable string for MVVM
/// (clear on navigation); prefer short-lived binding only on login forms.
/// </summary>
public partial class PasswordBoxWithEyeToggle : UserControl
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(
            nameof(Password),
            typeof(string),
            typeof(PasswordBoxWithEyeToggle),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordChanged));

    public static readonly DependencyProperty IsPasswordVisibleProperty =
        DependencyProperty.Register(
            nameof(IsPasswordVisible),
            typeof(bool),
            typeof(PasswordBoxWithEyeToggle),
            new PropertyMetadata(false, OnIsPasswordVisibleChanged));

    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.Register(
            nameof(Watermark),
            typeof(string),
            typeof(PasswordBoxWithEyeToggle),
            new PropertyMetadata("Password"));

    private bool _syncing;

    public PasswordBoxWithEyeToggle()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyVisibilityState();
    }

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value ?? string.Empty);
    }

    public bool IsPasswordVisible
    {
        get => (bool)GetValue(IsPasswordVisibleProperty);
        set => SetValue(IsPasswordVisibleProperty, value);
    }

    public string Watermark
    {
        get => (string)GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>Clears both editors and the bound password (call after successful sign-in).</summary>
    public void Clear()
    {
        _syncing = true;
        try
        {
            MaskedBox.Password = string.Empty;
            PlainBox.Text = string.Empty;
            Password = string.Empty;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBoxWithEyeToggle control || control._syncing)
        {
            return;
        }

        var value = e.NewValue as string ?? string.Empty;
        control._syncing = true;
        try
        {
            if (control.IsPasswordVisible)
            {
                if (control.PlainBox.Text != value)
                {
                    control.PlainBox.Text = value;
                    control.PlainBox.CaretIndex = value.Length;
                }
            }
            else if (control.MaskedBox.Password != value)
            {
                control.MaskedBox.Password = value;
            }
        }
        finally
        {
            control._syncing = false;
        }
    }

    private static void OnIsPasswordVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBoxWithEyeToggle control)
        {
            control.ApplyVisibilityState(focusAfterToggle: true);
        }
    }

    private void ApplyVisibilityState(bool focusAfterToggle = false)
    {
        _syncing = true;
        try
        {
            if (IsPasswordVisible)
            {
                PlainBox.Text = MaskedBox.Password;
                PlainBox.Visibility = Visibility.Visible;
                MaskedBox.Visibility = Visibility.Collapsed;
                EyeOpen.Visibility = Visibility.Collapsed;
                EyeClosed.Visibility = Visibility.Visible;
                ToggleButton.ToolTip = "Hide password";
                if (focusAfterToggle)
                {
                    PlainBox.Focus();
                    PlainBox.CaretIndex = PlainBox.Text.Length;
                    Keyboard.Focus(PlainBox);
                }
            }
            else
            {
                MaskedBox.Password = PlainBox.Text;
                MaskedBox.Visibility = Visibility.Visible;
                PlainBox.Visibility = Visibility.Collapsed;
                EyeOpen.Visibility = Visibility.Visible;
                EyeClosed.Visibility = Visibility.Collapsed;
                ToggleButton.ToolTip = "Show password";
                if (focusAfterToggle)
                {
                    MaskedBox.Focus();
                    Keyboard.Focus(MaskedBox);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void MaskedBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            Password = MaskedBox.Password;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void PlainBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            Password = PlainBox.Text;
            MaskedBox.Password = PlainBox.Text;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        IsPasswordVisible = !IsPasswordVisible;
}
