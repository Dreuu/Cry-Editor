﻿using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Crying
{
    // http://www.romhacking.net/documents/[462]sappy.txt
    // http://www.pokecommunity.com/showthread.php?t=321868

    // cry table format:
    // 12 bytes entries
    // 4 (20 3c 00 00) changing to 00 3c 00 00 allows higher quality??
    // 4 pointer to cry sample
    // 4 sample shape (ff 00 ff 00 preferred)

    // sample format:
    // 16 byte header followed by variable PCM data
    // 1 (00)
    // 1 (00 = uncompressed, 01 = compressed)
    // 1 (00)
    // 1 (00 = unlooped, 40 = looped)
    // 4 pitch adjustment = sample rate * 1024
    // 4 loop relative to start (not allowed for cries!)
    // 4 size of sample

    public partial class MainForm : Form
    {
        ROM rom;
        Settings roms;

        int pokemonCount;
        int cryTable, hoennCryOrder;

        Cry cry = new Cry();

        public MainForm()
        {
            InitializeComponent();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // load ROMs info at startup
            try
            {
                roms = Settings.FromFile("ROMs.ini", "ini");
            }
            catch
            {
                MessageBox.Show("Unable to load ROMs.ini!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }
        
        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            rom?.Dispose();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.Title = "Open ROM";
            openFileDialog1.Filter = "GBA ROMs|*.gba";

            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                // open the ROM file
                var temp = new ROM(openFileDialog1.FileName);

                // check for a valid ROM code
                if (!roms.ContainsSection(temp.Code))
                {
                    MessageBox.Show($"ROM type {temp.Code} is not supported!", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    temp.Dispose();
                    return;
                }

                // copy temp to rom, closing old one first
                rom?.Dispose();
                rom = temp;

                // get some basic info
                pokemonCount = roms.GetInt32(rom.Code, "NumberOfPokemon", 10);
                cryTable = roms.GetInt32(rom.Code, "CryData", 16);
                hoennCryOrder = roms.GetInt32(rom.Code, "HoennCryOrder", 16);

                // valid ROM opened, load all necessary data
                {
                    // load Pokemon names
                    int firstPokemonName = roms.GetInt32(rom.Code, "PokemonNames", 16);
                    rom.Seek(firstPokemonName);

                    listBox1.Items.Clear();
                    listBox1.Items.AddRange(rom.ReadTextTable(11, pokemonCount));
                }
            }
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (rom == null || cry.Offset == 0) return;

            SaveCry();
            rom.Save();
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rom?.Dispose();
            rom = null;

            listBox1.Items.Clear();
            richTextBox1.Text = "";
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void importToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cry.Offset == 0) return;

            openFileDialog1.Title = "Import Cry";
            openFileDialog1.Filter = "Wave Files|*.wav";

            if (openFileDialog1.ShowDialog() != DialogResult.OK) return;

            // TODO: support more formats
            try
            {
                ImportCry(openFileDialog1.FileName);
                DisplayCry(cry.Index, listBox1.SelectedIndex);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"error: {ex.Message}");
            }
        }

        private void exportToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cry.Offset == 0) return;

            saveFileDialog1.Title = "Export Cry";
            saveFileDialog1.Filter = "Wave Files|*.wav";

            if (saveFileDialog1.ShowDialog() != DialogResult.OK) return;

            // TODO: support more formats
            ExportCry(saveFileDialog1.FileName);
        }

        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (rom == null || rom.FilePath == string.Empty) return;

            // get pokemon index
            int pokemonIndex = listBox1.SelectedIndex;

            // get cry index
            int tableIndex = GetCryIndex(pokemonIndex);
            if (tableIndex == -1)
            {
                richTextBox1.Text = $"POKéMON {pokemonIndex}\nNO CRY";
                return;
            }

            // load cry at index
            LoadCry(tableIndex);

            // cry loaded, output
            DisplayCry(tableIndex, pokemonIndex);
        }

        int GetCryIndex(int pokemonIndex)
        {
            if (pokemonIndex == 0)
                return -1;

            // get table index
            int tableIndex = pokemonIndex - 1;

            // beyond Celebi, load from order table
            if (pokemonIndex >= 277)    // 277 is Treecko
            {
                rom.Seek(hoennCryOrder + (pokemonIndex - 277) * 2);
                tableIndex = rom.ReadUInt16();
            }

            // pokemon between Celebi and Treecko have no cry
            if (pokemonIndex > 251 && pokemonIndex < 277)
            {
                //tableIndex = 387 + (pokemonIndex - 251);
                return -1;
            }

            // pokemon beyond Chimecho skip the ???/Unown slots
            if (pokemonIndex > 411)
            {
                //tableIndex += 24;
            }

            return tableIndex;
        }

        bool LoadCry(int index)
        {
            if (rom == null)
                return false;

            // load cry table entry
            rom.Seek(cryTable + index * 12);
            var someValue = rom.ReadInt32();
            var cryOffset = rom.ReadPointer();
            var cryShape = rom.ReadInt32();

            if (cryOffset == 0)
                return false;

            // load cry data
            rom.Seek(cryOffset);

            cry.Offset = cryOffset;
            cry.Index = index;
            cry.Compressed = rom.ReadUInt16() == 0x0001;
            cry.Looped = rom.ReadUInt16() == 0x4000;
            cry.SampleRate = rom.ReadInt32() >> 10;
            cry.LoopStart = rom.ReadInt32();
            cry.Size = rom.ReadInt32() + 1;

            if (!cry.Compressed)
            {
                // uncompressed, 1 sample per 1 byte of size
                cry.Data = new sbyte[cry.Size];
                for (int i = 0; i < cry.Size; i++)
                    cry.Data[i] = rom.ReadSByte();
            }
            else
            {
                // compressed, a bit of a hassle
                var lookup = new sbyte[] { 0, 1, 4, 9, 16, 25, 36, 49, -64, -49, -36, -25, -16, -9, -4, -1 };

                int alignment = 0, size = 0;
                sbyte pcmLevel = 0;

                var data = new List<sbyte>();
                for (;;)
                {
                    if (alignment == 0)
                    {
                        pcmLevel = rom.ReadSByte();
                        data.Add(pcmLevel);

                        alignment = 0x20;
                    }

                    var input = rom.ReadByte();
                    if (alignment < 0x20)
                    {
                        // first nybble
                        pcmLevel += lookup[input >> 4];
                        data.Add(pcmLevel);
                    }

                    // second nybble
                    pcmLevel += lookup[input & 0xF];
                    data.Add(pcmLevel);

                    // exit when currentSize >= cry.Size
                    size += 2;
                    if (size >= cry.Size) break;

                    alignment--;
                }

                cry.Data = data.ToArray();
            }
            return true;
        }

        void SaveCry()
        {
            if (rom == null || cry.Offset == 0) return;
            //var lookup = new byte[] { 0x0, 0x1, 0x4, 0x9, 0x10, 0x19, 0x24, 0x31, 0xC0, 0xCF, 0xDC, 0xE7, 0xF0, 0xF7, 0xFC, 0xFF };
            var lookup = new sbyte[] { 0, 1, 4, 9, 16, 25, 36, 49, -64, -49, -36, -25, -16, -9, -4, -1 };

            // compressed cries are not allowed
            //cry.Compressed = false;

            // copy cry data to be written
            var data = new List<byte>();
            if (cry.Compressed)
            {
                // data is compressed in blocks of 1 + 0x20 bytes at a time
                // first byte is normal signed PCM data
                // following 0x20 bytes are compressed based on previous value
                // (for a value not in lookup table, closest value will be chosen instead)
                Console.WriteLine("compressed");

                // each block has 0x40 samples
                var blockCount = cry.Data.Length / 0x40;
                if (cry.Data.Length % 0x40 > 0) blockCount++;

                var blocks = new byte[blockCount][];
                for (int n = 0; n < blockCount; n++)
                {
                    // create new block
                    blocks[n] = new byte[0x21];
                    int i = n * 0x40;
                    int k = 0;

                    // set first value
                    blocks[n][k++] = (byte)cry.Data[i];
                    sbyte pcm = cry.Data[i++];

                    for (int j = 1; j < 0x40 && i < cry.Data.Length; j++)
                    {
                        // get current sample
                        var sample = cry.Data[i++];

                        // difference between previous sample and this
                        var diff = sample - pcm;

                        // check for a perfect match in lookup table
                        var lookupI = -1;
                        for (int x = 0; x < 16; x++)
                        {
                            if (lookup[x] == diff)
                            {
                                lookupI = x;
                                break;
                            }
                        }

                        // search for the closest match in the table
                        if (lookupI == -1)
                        {   
                            var bestDiff = 255;
                            for (int x = 0; x < 16; x++)
                            {
                                if (Math.Abs(lookup[x] - diff) < bestDiff)
                                {
                                    lookupI = x;
                                    bestDiff = Math.Abs(lookup[x] - diff);
                                }
                            }
                        }

                        // set value in block
                        // on an odd value, increase position in block
                        if (j % 2 == 0)
                            blocks[n][k] |= (byte)(lookupI << 4);
                        else
                            blocks[n][k++] |= (byte)lookupI;

                        // set previous
                        pcm = sample;
                    }
                }

                for (int n = 0; n < blockCount; n++)
                    data.AddRange(blocks[n]);
            }
            else
            {
                // uncompressed, copy directly to data
                Console.WriteLine("uncompressed");
                foreach (var s in cry.Data)
                    data.Add((byte)s);
            }

            // determine if cry requires repointing
            if (cry.Size < data.Count)
            {
                Console.WriteLine("repoint required");
            //    return;
            }

            // write cry
            rom.Seek(cry.Offset);
            rom.WriteUInt16((ushort)(cry.Compressed ? 1 : 0));
            rom.WriteUInt16((ushort)(cry.Looped ? 0x4000 : 0));
            rom.WriteInt32(cry.SampleRate << 10);
            rom.WriteInt32(cry.LoopStart);
            rom.WriteInt32(cry.Data.Length - 1);
            rom.WriteBytes(data.ToArray());

            // write cry table entry
            rom.Seek(cryTable + cry.Index * 12);
            rom.WriteUInt32(cry.Compressed ? 0x00003C20u : 0x00003C00u);
            rom.WritePointer(cry.Offset);
            rom.WriteUInt32(0x00FF00FFu);
        }

        void ExportCry(string filename)
        {
            if (cry.Offset == 0) return;

            // http://www-mmsp.ece.mcgill.ca/documents/audioformats/wave/wave.html
            // http://soundfile.sapp.org/doc/WaveFormat/
            using (var writer = new BinaryWriter(File.Create(filename)))
            {
                // RIFF header
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));  // file ID
                writer.Write(0);                                // file size placeholder
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));  // format

                // fmt chunk
                writer.Write(Encoding.ASCII.GetBytes("fmt "));  // chunk ID
                writer.Write(16);                               // chunk size, 16 for PCM
                writer.Write((ushort)1);                        // format: 1 = wave_format_pcm
                writer.Write((ushort)1);                        // channel count
                writer.Write(cry.SampleRate);                   // sample rate
                writer.Write(cry.SampleRate/* * 1 * 8 / 8*/);   // SampleRate * NumChannels * BitsPerSample/8
                writer.Write((ushort)1 /* * 8 / 8*/);           // NumChannels * BitsPerSample/8
                writer.Write((ushort)8);                        // bits per sample

                // data chunk
                writer.Write(Encoding.ASCII.GetBytes("data"));  // chunk ID
                writer.Write(cry.Data.Length);                  // chunk size
                foreach (var sample in cry.Data)
                    writer.Write((byte)(sample + 0x80));        // wave PCM is unsigned unlike GBA PCM which is

                // fix header
                writer.Seek(4, SeekOrigin.Begin);
                writer.Write((int)writer.BaseStream.Length - 8);
            }
        }

        void ImportCry(string filename)
        {
            if (cry.Offset == 0) return;

            // load a wave file
            using (var reader = new BinaryReader(File.OpenRead(filename)))
            {
                // read RIFF header
                if (reader.ReadUInt32() != 0x46464952)
                    throw new Exception("This is not a WAVE file!");
                if (reader.ReadInt32() + 8 != reader.BaseStream.Length)
                    throw new Exception("Invalid file length!");
                if (reader.ReadUInt32() != 0x45564157)
                    throw new Exception("This is not a WAVE file!");

                // read fmt chunk
                if (reader.ReadUInt32() != 0x20746D66)
                    throw new Exception("Expected fmt chunk!");
                if (reader.ReadInt32() != 16)
                    throw new Exception("Invalid fmt chunk!");
                if (reader.ReadInt16() != 1)          // only PCM format allowed
                    throw new Exception("Cry must be in PCM format!");
                if (reader.ReadInt16() != 1)          // only 1 channel allowed
                    throw new Exception("Cry cannot have more than one channel!");
                cry.SampleRate = reader.ReadInt32();
                if (reader.ReadInt32() != cry.SampleRate)
                    throw new Exception("Invalid fmt chunk!");
                reader.ReadUInt16();
                var bitsPerSample = reader.ReadUInt16();
                if (bitsPerSample != 8)              // for now, only 8 bit PCM data
                    throw new Exception($"Cries must be 8-bit WAVE files! Got {bitsPerSample}-bit instead.");

                // data chunk
                if (reader.ReadUInt32() != 0x61746164)
                    throw new Exception("Expected data chunk!!");
                var dataSize = reader.ReadInt32();

                cry.Data = new sbyte[dataSize];
                for (int i = 0; i < dataSize; i++)  // read 8-bit unsigned PCM and convert to GBA signed form
                    cry.Data[i] = (sbyte)(reader.ReadByte() - 128);
            }

            // resetting some other properties just in case
            cry.Looped = false;
            cry.LoopStart = 0;
        }

        void DisplayCry(int tableIndex, int pokemonIndex)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"POKéMON {pokemonIndex}");
            sb.AppendLine($"CRY {tableIndex}");
            sb.AppendLine($"Offset: 0x{cry.Offset:X6}");
            sb.AppendLine($"Sample Rate: {cry.SampleRate} Hz");

            sb.AppendLine("Data:");
            foreach (var b in cry.Data)
            {
                sb.Append($"{b:X2} ");
            }
            sb.AppendLine();

            richTextBox1.Text = sb.ToString();
        }
    }
}