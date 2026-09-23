using System.Windows;
using System.Windows.Controls;

namespace ScreenTranslator;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty BindPasswordProperty=DependencyProperty.RegisterAttached(
        "BindPassword",typeof(bool),typeof(PasswordBoxBinding),new PropertyMetadata(false,OnBindChanged));
    public static readonly DependencyProperty BoundPasswordProperty=DependencyProperty.RegisterAttached(
        "BoundPassword",typeof(string),typeof(PasswordBoxBinding),new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,OnPasswordChanged));
    private static readonly DependencyProperty UpdatingProperty=DependencyProperty.RegisterAttached("Updating",typeof(bool),typeof(PasswordBoxBinding));
    public static void SetBindPassword(DependencyObject value,bool enabled)=>value.SetValue(BindPasswordProperty,enabled);
    public static bool GetBindPassword(DependencyObject value)=>(bool)value.GetValue(BindPasswordProperty);
    public static void SetBoundPassword(DependencyObject value,string password)=>value.SetValue(BoundPasswordProperty,password);
    public static string GetBoundPassword(DependencyObject value)=>(string)value.GetValue(BoundPasswordProperty);
    private static void OnBindChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        if(d is not PasswordBox box)return; box.PasswordChanged-=HandlePasswordChanged;if((bool)e.NewValue)box.PasswordChanged+=HandlePasswordChanged;
    }
    private static void HandlePasswordChanged(object sender,RoutedEventArgs e)
    {
        var box=(PasswordBox)sender;box.SetValue(UpdatingProperty,true);SetBoundPassword(box,box.Password);box.SetValue(UpdatingProperty,false);
    }
    private static void OnPasswordChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        if(d is PasswordBox box&&!(bool)box.GetValue(UpdatingProperty))box.Password=e.NewValue as string??"";
    }
}
