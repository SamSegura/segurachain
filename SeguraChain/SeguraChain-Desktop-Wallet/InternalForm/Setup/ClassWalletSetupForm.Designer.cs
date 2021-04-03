
namespace SeguraChain_Desktop_Wallet.InternalForm.Setup
{
    partial class ClassWalletSetupForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ClassWalletSetupForm));
            this.checkBoxWalletSetupEnablePublicMode = new System.Windows.Forms.CheckBox();
            this.textBoxPublicIp = new System.Windows.Forms.TextBox();
            this.textBoxPublicPort = new System.Windows.Forms.TextBox();
            this.panel1 = new System.Windows.Forms.Panel();
            this.panel2 = new System.Windows.Forms.Panel();
            this.label1 = new System.Windows.Forms.Label();
            this.panel1.SuspendLayout();
            this.SuspendLayout();
            // 
            // checkBoxWalletSetupEnablePublicMode
            // 
            this.checkBoxWalletSetupEnablePublicMode.AutoSize = true;
            this.checkBoxWalletSetupEnablePublicMode.ForeColor = System.Drawing.Color.Ivory;
            this.checkBoxWalletSetupEnablePublicMode.Location = new System.Drawing.Point(27, 31);
            this.checkBoxWalletSetupEnablePublicMode.Name = "checkBoxWalletSetupEnablePublicMode";
            this.checkBoxWalletSetupEnablePublicMode.Size = new System.Drawing.Size(305, 19);
            this.checkBoxWalletSetupEnablePublicMode.TabIndex = 0;
            this.checkBoxWalletSetupEnablePublicMode.Text = "WALLET_SETUP_CHECKBOX_ENABLE_PUBLIC_MODE";
            this.checkBoxWalletSetupEnablePublicMode.UseVisualStyleBackColor = true;
            // 
            // textBoxPublicIp
            // 
            this.textBoxPublicIp.Location = new System.Drawing.Point(27, 78);
            this.textBoxPublicIp.Name = "textBoxPublicIp";
            this.textBoxPublicIp.ReadOnly = true;
            this.textBoxPublicIp.Size = new System.Drawing.Size(305, 23);
            this.textBoxPublicIp.TabIndex = 1;
            // 
            // textBoxPublicPort
            // 
            this.textBoxPublicPort.Location = new System.Drawing.Point(27, 134);
            this.textBoxPublicPort.Name = "textBoxPublicPort";
            this.textBoxPublicPort.Size = new System.Drawing.Size(305, 23);
            this.textBoxPublicPort.TabIndex = 2;
            // 
            // panel1
            // 
            this.panel1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(77)))), ((int)(((byte)(104)))), ((int)(((byte)(145)))));
            this.panel1.Controls.Add(this.checkBoxWalletSetupEnablePublicMode);
            this.panel1.Controls.Add(this.textBoxPublicPort);
            this.panel1.Controls.Add(this.textBoxPublicIp);
            this.panel1.Location = new System.Drawing.Point(12, 55);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(364, 186);
            this.panel1.TabIndex = 3;
            // 
            // panel2
            // 
            this.panel2.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(77)))), ((int)(((byte)(104)))), ((int)(((byte)(145)))));
            this.panel2.Location = new System.Drawing.Point(12, 262);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(364, 170);
            this.panel2.TabIndex = 4;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            this.label1.ForeColor = System.Drawing.Color.Ivory;
            this.label1.Location = new System.Drawing.Point(263, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(270, 21);
            this.label1.TabIndex = 5;
            this.label1.Text = "WALLET_SETUP_LABEL_TITLE_TEXT";
            // 
            // ClassWalletSetupForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(49)))), ((int)(((byte)(55)))), ((int)(((byte)(64)))));
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.panel2);
            this.Controls.Add(this.panel1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Fixed3D;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "ClassWalletSetupForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "WALLET_SETUP_FORM_TITLE";
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.CheckBox checkBoxWalletSetupEnablePublicMode;
        private System.Windows.Forms.TextBox textBoxPublicIp;
        private System.Windows.Forms.TextBox textBoxPublicPort;
        private System.Windows.Forms.Panel panel1;
        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.Label label1;
    }
}