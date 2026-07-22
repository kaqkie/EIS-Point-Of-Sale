using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PointOfSale.App.Controls;

/// <summary>
/// Secure password entry with a press-and-hold eye control that reveals text while held
/// and remasks immediately on release, mouse leave, lost capture, or focus leaving the control.
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
    private bool _peekHeld;

    public PasswordBoxWithEyeToggle()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyVisibilityState();
        Unloaded += (_, _) => ForceMask();
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
        ForceMask();
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
            control.ApplyVisibilityState(focusAfterToggle: false);
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
                ToggleButton.ToolTip = "Release to hide password";
            }
            else
            {
                MaskedBox.Password = PlainBox.Text;
                MaskedBox.Visibility = Visibility.Visible;
                PlainBox.Visibility = Visibility.Collapsed;
                EyeOpen.Visibility = Visibility.Visible;
                EyeClosed.Visibility = Visibility.Collapsed;
                ToggleButton.ToolTip = "Hold to show password";
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

    private void BeginPeek()
    {
        if (_peekHeld)
        {
            return;
        }

        _peekHeld = true;
        IsPasswordVisible = true;
    }

    private void EndPeek()
    {
        if (!_peekHeld && !IsPasswordVisible)
        {
            return;
        }

        _peekHeld = false;
        if (ToggleButton.IsMouseCaptured)
        {
            ToggleButton.ReleaseMouseCapture();
        }

        IsPasswordVisible = false;
    }

    private void ForceMask()
    {
        _peekHeld = false;
        if (ToggleButton.IsMouseCaptured)
        {
            ToggleButton.ReleaseMouseCapture();
        }

        if (IsPasswordVisible)
        {
            IsPasswordVisible = false;
        }
    }

    private void ToggleButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ToggleButton.CaptureMouse();
        BeginPeek();
        e.Handled = true;
    }

    private void ToggleButton_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPeek();
        e.Handled = true;
    }

    private void ToggleButton_OnMouseLeave(object sender, MouseEventArgs e)
    {
        // If the pointer leaves without an active capture hold, remask immediately.
        if (!_peekHeld)
        {
            ForceMask();
            return;
        }

        // While held with capture, keep peeking until mouse up / lost capture.
        if (!ToggleButton.IsMouseCaptured)
        {
            EndPeek();
        }
    }

    private void ToggleButton_OnLostMouseCapture(object sender, MouseEventArgs e) => EndPeek();

    private void ToggleButton_OnPreviewTouchDown(object sender, TouchEventArgs e)
    {
        BeginPeek();
        e.Handled = true;
    }

    private void ToggleButton_OnPreviewTouchUp(object sender, TouchEventArgs e)
    {
        EndPeek();
        e.Handled = true;
    }

    private void OnIsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            ForceMask();
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
}
