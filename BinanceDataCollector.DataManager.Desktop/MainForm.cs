namespace BinanceDataCollector.DataManager.Desktop
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            InitializeComponent();
        }

        private void TradesDatabaseMenu_Click(object sender, EventArgs e)
        {
            var tradesDatabase = new TradesDatabase();
            tradesDatabase.FormClosed += (s, args) => { Show(); };
            Hide();
            tradesDatabase.Show();
        }

        private void TradesCSVMenu_Click(object sender, EventArgs e)
        {
            var csvForm = new CsvTrades();
            csvForm.FormClosed += (s, args) => { Show(); };
            Hide();
            csvForm.Show();
        }

        private void OhlcvCsv_Click(object sender, EventArgs e)
        {
            var ohlcvCsv = new OhlcvCsv();
            ohlcvCsv.FormClosed += (s, args) => { Show(); };
            Hide();
            ohlcvCsv.Show();
        }

        private void OhlcvDatabase_Click(object sender, EventArgs e)
        {
            var ohlcvDatabase = new OhlcvDatabase();
            ohlcvDatabase.FormClosed += (s, args) => { Show(); };
            Hide();
            ohlcvDatabase.Show();
        }
    }
}
