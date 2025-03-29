using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace JungleTracker
{
    public partial class ConsentWindow : Window
    {
        public bool UserConsent { get; private set; } = false;

        public ConsentWindow()
        {
            InitializeComponent();
        }

        private void AgreeButton_Click(object sender, RoutedEventArgs e)
        {
            UserConsent = true;
            this.DialogResult = true;
            this.Close();
        }

        private void DisagreeButton_Click(object sender, RoutedEventArgs e)
        {
            UserConsent = false;
            this.DialogResult = false;
            this.Close();
        }
    }
}
