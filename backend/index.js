/* eslint-disable no-undef */
import express from 'express';
import dotenv from 'dotenv';
dotenv.config();

const app = express();
const port = process.env.PORT || 3000;

app.get('/api', (req, res) => {
    const CONTRACTS = process.env.CONTRACTS || 0;
    res.json({ contracts: CONTRACTS });
});

app.listen(port, () => {
    console.log(`Server is running on port ${port}`);
});
