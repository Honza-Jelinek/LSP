import { createTheme } from '@mui/material/styles'

export const theme = createTheme({
  palette: {
    mode: 'dark',
    primary: { main: '#e50914' },
    background: {
      default: '#0b0b0f',
      paper: '#14141a',
    },
  },
  typography: {
    fontFamily: "system-ui, 'Segoe UI', Roboto, sans-serif",
  },
  shape: { borderRadius: 8 },
  components: {
    MuiButton: {
      defaultProps: { disableElevation: true },
    },
    MuiDrawer: {
      styleOverrides: {
        paper: { backgroundColor: '#0e0e12' },
      },
    },
  },
})
